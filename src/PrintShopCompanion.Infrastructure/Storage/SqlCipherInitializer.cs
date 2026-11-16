namespace PrintShopCompanion.Infrastructure.Storage;

using Microsoft.Extensions.Logging;

/// <summary>
/// Initializes the SQLCipher native provider before any SQLite connection
/// is opened. Must be called once at app startup, BEFORE any
/// <c>SqliteConnection</c> is created.
///
/// <para><b>How it works:</b></para>
/// <para>
/// <c>Microsoft.Data.Sqlite</c> uses <c>SQLitePCLRaw</c> under the hood to
/// load the native SQLite library. By default, it loads the plain
/// <c>e_sqlite3</c> binary which does NOT support encryption. The
/// <c>SQLitePCLRaw.bundle_e_sqlcipher</c> NuGet package includes a version
/// of the native library with the SQLCipher extension compiled in, and
/// registers it via <c>Batteries_V2.Init()</c>.
/// </para>
/// <para>
/// We must call <c>SQLitePCL.Batteries_V2.Init()</c> (from the
/// <c>SQLitePCLRaw.core</c> package, which is a transitive dependency of
/// <c>bundle_e_sqlcipher</c>) before ANY <c>SqliteConnection</c> is opened.
/// If we don't, <c>Microsoft.Data.Sqlite</c> picks the default provider
/// (plain <c>e_sqlite3</c>) and the <c>Password=</c> connection string
/// parameter is silently ignored — the database opens as plain text.
/// </para>
/// <para>
/// This class is registered as an <c>IHostedService</c> or called directly
/// from <c>App.OnStartup</c> before the DI host is built.
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
            // SQLitePCL.Batteries_V2.Init() registers the SQLCipher-capable
            // native provider. This is from the SQLitePCLRaw.core package
            // (transitive of bundle_e_sqlcipher).
            // The method name varies by version — try Batteries_V2 first,
            // then Batteries (the older API).
            try
            {
                // The bundle_e_sqlcipher package exports a Batteries_V2 class
                // in the SQLitePCL namespace. Calling Init() registers the
                // e_sqlcipher provider as the default.
                var batteriesType = Type.GetType("SQLitePCL.Batteries_V2, SQLitePCLRaw.core")
                                 ?? Type.GetType("SQLitePCL.Batteries_V2, SQLitePCLRaw.bundle_e_sqlcipher");
                if (batteriesType != null)
                {
                    var initMethod = batteriesType.GetMethod("Init", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
                    initMethod?.Invoke(null, null);
                    _encryptionAvailable = true;
                    logger?.LogInformation("SQLCipher native provider initialized (Batteries_V2.Init) — encryption available");
                }
                else
                {
                    // Fall back to the older Batteries API.
                    var legacyBatteries = Type.GetType("SQLitePCL.Batteries, SQLitePCLRaw.core");
                    if (legacyBatteries != null)
                    {
                        var initMethod = legacyBatteries.GetMethod("Init", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
                        initMethod?.Invoke(null, null);
                        _encryptionAvailable = true;
                        logger?.LogInformation("SQLCipher native provider initialized (legacy Batteries.Init) — encryption available");
                    }
                    else
                    {
                        logger?.LogWarning("Could not find SQLitePCL Batteries type — encryption may not be available");
                        _encryptionAvailable = false;
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to initialize SQLCipher native provider — database will be unencrypted");
                _encryptionAvailable = false;
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "SQLCipher initialization failed");
            _encryptionAvailable = false;
        }

        return _encryptionAvailable;
    }

    /// <summary>True if the SQLCipher native provider was successfully initialized.</summary>
    public static bool IsEncryptionAvailable => _encryptionAvailable;
}
