namespace PrintShopCompanion.Infrastructure.Storage;

using Microsoft.Extensions.Logging;

/// <summary>
/// Initializes the SQLCipher native provider before any SQLite connection
/// is opened. Must be called once at app startup, BEFORE any
/// <c>SqliteConnection</c> is created.
///
/// <para><b>How it works (v0.7.2):</b></para>
/// <para>
/// <c>Microsoft.Data.Sqlite</c> uses <c>SQLitePCLRaw</c> under the hood to
/// load the native SQLite library. The app references
/// <c>Microsoft.Data.Sqlite.Core</c> (which auto-registers NOTHING) plus
/// <c>SQLitePCLRaw.bundle_e_sqlcipher</c>, and this class sets the
/// <c>e_sqlcipher</c> provider directly via
/// <c>SQLitePCL.raw.SetProvider(new SQLite3Provider_e_sqlcipher())</c> —
/// no reflection, no Batteries indirection. Availability is then PROVEN by
/// opening an in-memory keyed connection and reading
/// <c>PRAGMA cipher_version</c>.
/// </para>
/// <para>
/// This class is called from the <c>SqliteJobQueue</c> and
/// <c>SqlitePendingOpsQueue</c> constructors (idempotent, thread-safe) so
/// the provider is always in place before the first connection.
/// </para>
/// </summary>
public static class SqlCipherInitializer
{
    private static int _initialized;
    private static bool _encryptionAvailable;

    /// <summary>
    /// Initialize the SQLCipher native provider. Safe to call multiple times —
    /// only the first call does the actual work.
    /// </summary>
    /// <returns>True if SQLCipher encryption is available; false if only
    /// plain SQLite is available (e.g. on Linux without the native binary).</returns>
    public static bool Initialize(ILogger? logger = null)
    {
        if (Interlocked.CompareExchange(ref _initialized, 1, 0) != 0)
            return _encryptionAvailable;

        try
        {
            // v0.7.2 fix: set the provider DIRECTLY. The previous version
            // used reflection to find SQLitePCL.Batteries_V2 in
            // "SQLitePCLRaw.core" and "SQLitePCLRaw.bundle_e_sqlcipher" —
            // NEITHER assembly contains that type (it lives in
            // SQLitePCLRaw.batteries_v2), so initialization ALWAYS failed in
            // the field and the queue refused to open ("SQLCipher native
            // provider is not available", user report 2026-11-26). On top of
            // that, the meta Microsoft.Data.Sqlite package shipped a SECOND
            // SQLitePCLRaw.batteries_v2 assembly registering the PLAIN
            // provider — same assembly identity, undefined bind race. The
            // app now references Microsoft.Data.Sqlite.Core (auto-registers
            // nothing) + bundle_e_sqlcipher, so e_sqlcipher is the only
            // provider candidate and this call is deterministic.
            try
            {
                SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlcipher());
            }
            catch (Exception ex)
            {
                // "Provider already set" or native-load failure — the probe
                // below, not this catch, decides the final verdict.
                logger?.LogDebug(ex, "SetProvider(e_sqlcipher) did not complete cleanly — probing anyway");
            }

            // Definitive availability probe: a SQLCipher-capable provider
            // answers PRAGMA cipher_version (verified experimentally with the
            // exact shipped package set: "4.5.2 community"). A plain provider
            // either errors on the unknown pragma or ignores Password= and
            // returns no row — both count as "not available".
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection(
                "Data Source=:memory:;Password=sqlcipher-availability-probe");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA cipher_version;";
            var version = cmd.ExecuteScalar()?.ToString();

            if (!string.IsNullOrWhiteSpace(version))
            {
                _encryptionAvailable = true;
                logger?.LogInformation("SQLCipher native provider ready — cipher_version {CipherVersion}", version);
            }
            else
            {
                _encryptionAvailable = false;
                logger?.LogWarning(
                    "SQLCipher probe returned no cipher_version — provider loaded but encryption is NOT available");
            }
        }
        catch (Exception ex)
        {
            _encryptionAvailable = false;
            logger?.LogWarning(ex, "SQLCipher initialization failed — encryption unavailable");
        }

        return _encryptionAvailable;
    }

    /// <summary>True if the SQLCipher native provider was successfully initialized.</summary>
    public static bool IsEncryptionAvailable => _encryptionAvailable;
}
