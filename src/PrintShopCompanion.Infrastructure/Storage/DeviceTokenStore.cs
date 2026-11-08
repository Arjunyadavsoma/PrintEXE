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
        if (!File.Exists(_path)) return null;
        try
        {
            var bytes = await File.ReadAllBytesAsync(_path, ct);
            var plain = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
            await using var ms = new MemoryStream(plain);
            return await JsonSerializer.DeserializeAsync<StoredCredentials>(ms, Json, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load credentials");
            return null;
        }
    }

    public async Task SaveAsync(StoredCredentials credentials, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        await using var ms = new MemoryStream();
        await JsonSerializer.SerializeAsync(ms, credentials, Json, ct);
        var plain = ms.ToArray();
        var cipher = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
        await File.WriteAllBytesAsync(_path, cipher, ct);
        _logger.LogInformation("Credentials persisted (encrypted, current-user scope)");
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
