namespace PrintShopCompanion.Infrastructure.Storage;

using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrintShopCompanion.Core.Contracts;
using PrintShopCompanion.Core.Models;
using PrintShopCompanion.Core.Options;

/// <summary>
/// Stores the shopkeeper's session (access + refresh token) and the device
/// token (for the background print agent) encrypted with Windows DPAPI
/// (current-user scope). The credentials never touch disk in cleartext.
///
/// <para><b>v0.5: uses LocalStorageLayout paths.</b> The device token is
/// stored at <c>%LocalAppData%\PrintShop\Companion\Security\device.dat</c>.
/// On first run, if the old v0.4.7 file at <c>%AppData%\PrintShop\Companion\
/// device.dat</c> exists, it is automatically moved to the new location.</para>
/// </summary>
public sealed class DeviceTokenStore : ICredentialStore
{
    private readonly string _path;
    private readonly ILogger<DeviceTokenStore> _logger;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public DeviceTokenStore(LocalStorageLayout layout, ILogger<DeviceTokenStore> logger)
    {
        _path = layout.DeviceTokenPath;
        _logger = logger;

        // v0.5 migration: move the old device.dat from %AppData% to the new
        // %LocalAppData%\...\Security\ location if it exists and the new
        // location doesn't.
        layout.EnsureDirectories();
        layout.MigrateDeviceToken();
    }

    public async Task<StoredCredentials?> LoadAsync(CancellationToken ct = default)
    {
        // Phase 3 (brief §5) — graceful recovery. If the primary file is
        // missing/corrupt, try the .bak that SaveAsync preserves until the new
        // file is verified. This covers the case where a crash interrupted the
        // atomic move (extremely unlikely, but the brief requires recovery).
        var creds = await TryLoadAsync(_path, ct);
        if (creds is not null) return creds;

        var bakPath = _path + ".bak";
        if (File.Exists(bakPath))
        {
            _logger.LogWarning("Primary credential file failed to load — falling back to .bak");
            creds = await TryLoadAsync(bakPath, ct);
            if (creds is not null)
            {
                // Promote the .bak to primary so subsequent loads don't need the fallback.
                try { File.Copy(bakPath, _path, overwrite: true); } catch { /* best effort */ }
                return creds;
            }
        }
        return null;
    }

    private async Task<StoredCredentials?> TryLoadAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var bytes = await File.ReadAllBytesAsync(path, ct);
            var plain = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
            await using var ms = new MemoryStream(plain);
            return await JsonSerializer.DeserializeAsync<StoredCredentials>(ms, Json, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load credentials from {Path}", path);
            return null;
        }
    }

    public async Task SaveAsync(StoredCredentials credentials, CancellationToken ct = default)
    {
        // Phase 3 (brief §5) — production-safe credential write:
        // 1. Validate the credentials serialize + DPAPI-encrypt BEFORE touching
        //    the existing file. A null/empty device token must NOT overwrite a
        //    valid one (the brief: "Never overwrite a valid token with an empty
        //    or malformed value").
        // 2. Write to a temp file in the SAME directory (so the rename is
        //    atomic on Windows — same-volume moves are atomic; cross-volume
        //    would fall back to copy+delete which is NOT atomic).
        // 3. Back up the existing file to <path>.bak before the rename. Keep
        //    the .bak until the new file is verified loadable (the brief:
        //    "Preserve the old credential until the new credential is safely
        //    written and verified").
        // 4. Verify the new file decrypts + deserializes (round-trip). If it
        //    fails, restore the .bak (we never want to leave the user with no
        //    credential).
        // 5. Delete the .bak only after the round-trip succeeds.
        if (credentials is null)
            throw new ArgumentNullException(nameof(credentials), "Cannot save null credentials — use ClearAsync to remove them.");
        if (credentials.DeviceToken is { } dt && string.IsNullOrWhiteSpace(dt.Token))
            throw new ArgumentException("Refusing to save credentials with an empty device token (would overwrite a valid one).", nameof(credentials));

        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // Step 1: serialize + encrypt to memory (fail fast if malformed).
        await using var ms = new MemoryStream();
        await JsonSerializer.SerializeAsync(ms, credentials, Json, ct);
        var plain = ms.ToArray();
        if (plain.Length == 0)
            throw new InvalidOperationException("Serialized credentials are empty — refusing to write an empty file.");
        var cipher = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);

        // Step 2: write to a temp file in the SAME directory (atomic rename target).
        var tempPath = _path + ".tmp";
        await File.WriteAllBytesAsync(tempPath, cipher, ct);

        // Step 3: back up the existing file (if any) to .bak.
        var bakPath = _path + ".bak";
        var hadExisting = File.Exists(_path);
        if (hadExisting)
        {
            // Overwrite any stale .bak with the current file's contents.
            File.Copy(_path, bakPath, overwrite: true);
        }

        // Step 4: atomic rename temp → final (same-volume, atomic on Windows).
        try
        {
            File.Move(tempPath, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Atomic move of credentials failed — restoring from .bak if present");
            // Try to restore the .bak (the move failed, so the original may be gone).
            if (hadExisting && File.Exists(bakPath))
            {
                try { File.Copy(bakPath, _path, overwrite: true); } catch { /* best effort */ }
            }
            // Clean up the temp file if it's still there.
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best effort */ }
            throw;
        }

        // Step 5: verify the new file round-trips (decrypt + deserialize).
        try
        {
            await using var verifyMs = new MemoryStream(await File.ReadAllBytesAsync(_path, ct));
            var verifyPlain = ProtectedData.Unprotect(verifyMs.ToArray(), null, DataProtectionScope.CurrentUser);
            await using var verifyIn = new MemoryStream(verifyPlain);
            var verified = await JsonSerializer.DeserializeAsync<StoredCredentials>(verifyIn, Json, ct);
            if (verified is null)
                throw new InvalidOperationException("Round-trip verification returned null — restored .bak");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Round-trip verification of new credentials failed — restoring .bak");
            if (hadExisting && File.Exists(bakPath))
            {
                try { File.Copy(bakPath, _path, overwrite: true); } catch { /* best effort */ }
            }
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best effort */ }
            throw new InvalidOperationException("Credential round-trip verification failed; previous credential restored from .bak if available.", ex);
        }

        // Step 6: round-trip succeeded — delete the .bak (the new credential is durable).
        if (hadExisting)
        {
            try { if (File.Exists(bakPath)) File.Delete(bakPath); } catch { /* best effort — .bak cleanup is non-fatal */ }
        }
        _logger.LogInformation("Credentials persisted atomically (encrypted, current-user scope, round-trip verified)");
    }

    public Task ClearAsync(CancellationToken ct = default)
    {
        if (File.Exists(_path))
        {
            try { File.Delete(_path); _logger.LogInformation("Credentials cleared"); } catch { /* ignore */ }
        }
        return Task.CompletedTask;
    }
}
