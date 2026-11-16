namespace PrintShopCompanion.Infrastructure.Storage;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Generates, stores, and loads the per-installation SQLCipher encryption
/// key for the local SQLite queue database. The key is:
/// <list type="bullet">
///   <item>32 bytes (256 bits) of cryptographic randomness from <c>RandomNumberGenerator</c>.</item>
///   <item>Stored DPAPI-encrypted with <c>DataProtectionScope.CurrentUser</c> at
///   <c>%LocalAppData%\PrintShop\Companion\Security\queue.key</c>.</item>
///   <item>Per-installation: a fresh key is generated on first run. If the
///   key file is lost, the SQLite DB is unreadable (by design — the data is
///   encrypted at rest and the key is the only way in).</item>
///   <item>Tied to the Windows user: copying both <c>queue.db</c> and
///   <c>queue.key</c> to another machine (or another user on the same
///   machine) yields unusable bytes — DPAPI is keyed to the user + machine.</item>
/// </list>
/// </summary>
public sealed class QueueKeyStore
{
    private readonly string _keyPath;
    private readonly ILogger<QueueKeyStore> _logger;
    private byte[]? _cachedKey;

    public QueueKeyStore(LocalStorageLayout layout, ILogger<QueueKeyStore> logger)
    {
        _keyPath = layout.QueueKeyPath;
        _logger = logger;
    }

    /// <summary>
    /// Load the SQLCipher key from the DPAPI-encrypted file, or generate
    /// a new key on first run. The key is cached in memory for the
    /// lifetime of the process.
    /// </summary>
    /// <returns>The 32-byte SQLCipher key.</returns>
    public byte[] LoadOrGenerateKey()
    {
        if (_cachedKey is not null) return _cachedKey;

        // Ensure the Security/ directory exists.
        Directory.CreateDirectory(Path.GetDirectoryName(_keyPath)!);

        if (File.Exists(_keyPath))
        {
            try
            {
                var encrypted = File.ReadAllBytes(_keyPath);
                _cachedKey = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                _logger.LogInformation("Loaded SQLCipher key from {Path} ({Bytes} bytes)",
                    _keyPath, _cachedKey.Length);
                return _cachedKey;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load SQLCipher key from {Path} — the file may be " +
                    "corrupt or this is a different Windows user. Generating a new key (the old " +
                    "queue.db will be unreadable — see docs/UPGRADE_FROM_V047.md for recovery).",
                    _keyPath);
                // Fall through to generate a new key. The existing queue.db
                // will need to be re-migrated from the v0.4.7 backup.
            }
        }

        // Generate a new 32-byte key (256 bits — SQLCipher uses AES-256-CBC).
        _cachedKey = RandomNumberGenerator.GetBytes(32);
        var encryptedKey = ProtectedData.Protect(_cachedKey, null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(_keyPath, encryptedKey);
        _logger.LogInformation("Generated new SQLCipher key and persisted to {Path} ({Bytes} bytes encrypted)",
            _keyPath, encryptedKey.Length);
        return _cachedKey;
    }

    /// <summary>
    /// Returns the SQLCipher key as a hex string suitable for use as the
    /// <c>Password</c> parameter in a SQLite connection string. SQLCipher
    /// accepts either a raw passphrase (which it derives via PBKDF2) OR
    /// a hex-encoded key prefixed with <c>x'</c> and suffixed with <c>'</c>.
    /// We use the hex form to avoid PBKDF2 derivation overhead (the key
    /// is already cryptographically random — no derivation needed).
    /// </summary>
    public string GetKeyForConnectionString()
    {
        var key = LoadOrGenerateKey();
        // SQLCipher raw key format: x'<64 hex chars>'
        // The x'' prefix tells SQLCipher to use the key as-is (no PBKDF2).
        return "x'" + Convert.ToHexString(key).ToLowerInvariant() + "'";
    }

    /// <summary>
    /// Clear the cached key (called on shutdown). The key file on disk is
    /// NOT deleted — it's needed on next startup.
    /// </summary>
    public void ClearCache()
    {
        if (_cachedKey is not null)
        {
            Array.Clear(_cachedKey, 0, _cachedKey.Length);
            _cachedKey = null;
        }
    }
}
