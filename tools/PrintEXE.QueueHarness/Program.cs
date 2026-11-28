// Phase 10.3 (Stages 4-5): queue harness for CI evidence generation.
//
// Creates and verifies REAL encrypted queue databases through the
// application's own production classes (QueueKeyStore/DPAPI, SqliteJobQueue/
// SQLCipher, LocalStorageLayout, DbErrorClassifier) — never fabricated
// database files. Used by installer/msi-validation.ps1 to close the
// Phase 10.2 gaps that need an app-created queue.db:
//   Stage 4: materialize a real encrypted queue DB (jobs, PDFs, states)
//   Stage 5: prove DPAPI key continuity + encrypted-data preservation
//            across an MSI upgrade with a machine-readable comparison
//
// Security: the SQLCipher key is NEVER printed, logged or written to the
// JSON output — only SHA-256 fingerprints of key bytes. All storage is
// under a harness-controlled root (default: the runner's app data root,
// isolated per CI job and wiped by the matrix harness).
//
// Exit codes: 0 = ok, 2 = failure, 3 = precondition/blocked (never a pass).

using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PrintShopCompanion.Core.Models;
using PrintShopCompanion.Core.Options;
using PrintShopCompanion.Infrastructure.Queue;
using PrintShopCompanion.Infrastructure.Storage;

internal static class Program
{
    private static int Main(string[] args)
    {
        // Transient file-lock retry: on an ephemeral runner, Defender's
        // real-time scan can hold a freshly-created database file for a few
        // seconds ("being used by another process", run 46160668069). This is
        // a bounded infrastructure-retry for ONE specific transient message —
        // never a logic-error retry, and the attempt count is visible.
        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                if (args.Length < 2) return Usage();
                var command = args[0].ToLowerInvariant();
                var dataRoot = GetArg(args, "--data-root") ?? throw new ArgumentException("--data-root is required");

                return command switch
                {
                    "seed" => RunSeed(dataRoot, args),
                    "verify" => RunVerify(dataRoot, args),
                    "integrity" => RunIntegrity(dataRoot),
                    _ => Usage(),
                };
            }
            catch (IOException ioEx) when (
                ioEx.Message.Contains("being used by another process", StringComparison.OrdinalIgnoreCase)
                && attempt < maxAttempts)
            {
                Console.Error.WriteLine($"HARNESS-RETRY: transient file lock (attempt {attempt}/{maxAttempts}), waiting 4 s");
                Thread.Sleep(4000);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("HARNESS-ERROR: " + ex.GetType().Name + ": " +
                    Truncate(ex.Message, 400));
                return 2;
            }
        }
        return 2;
    }

    private static int Usage()
    {
        Console.WriteLine("""
            PrintEXE.QueueHarness — real-queue CI evidence tool
              seed  --data-root <path> [--pending N] [--completed M] [--pdfs-dir <path>]
              verify --data-root <path> --expected <snapshot.json> [--expect-new-key=false]
              integrity --data-root <path>
            """);
        return 3;
    }

    private static string? GetArg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static LocalStorageLayout NewLayout(string root) =>
        new(Options.Create(new StorageOptions()), rootOverride: root);

    private static QueueKeyStore NewKeyStore(string root) =>
        new(NewLayout(root), NullLogger<QueueKeyStore>.Instance);

    private static SqliteJobQueue NewQueue(string root, DatabaseRecoveryState? state = null) =>
        new(Options.Create(new StorageOptions()), NewLayout(root),
            NewKeyStore(root), NullLogger<SqliteJobQueue>.Instance, state);

    private static string Sha256File(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    /// <summary>The 16-byte header hex — evidence, never key material.</summary>
    private static string Header16Hex(string dbPath)
    {
        using var fs = File.OpenRead(dbPath);
        var buf = new byte[16];
        var n = fs.Read(buf, 0, 16);
        if (n != 16) throw new IOException("queue.db too small for a header read");
        return Convert.ToHexString(buf);
    }

    private static string KeyFingerprint(string root)
    {
        var key = NewKeyStore(root).LoadOrGenerateKey();
        return Convert.ToHexString(SHA256.HashData(key));
    }

    private static PrintJobRecord MakeJob(string id, string title, JobStatus status) => new()
    {
        JobId = id,
        ShopId = "shop-phase103-ci",
        DeviceId = "device-phase103-ci",
        Title = title,
        Copies = 1,
        Status = status,
        CreatedAt = DateTimeOffset.UtcNow,
        QueuedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        OriginalFilename = id + ".pdf",
    };

    /// <summary>
    /// STAGE 4: create a real encrypted queue DB through the app's own
    /// EnqueueAsync (the same path a backend-dispatched job takes), attach
    /// real PDF files under the layout's job directories, and emit a JSON
    /// snapshot (no secrets).
    /// </summary>
    private static int RunSeed(string root, string[] args)
    {
        var pending = int.TryParse(GetArg(args, "--pending"), out var p) ? p : 1;
        var completed = int.TryParse(GetArg(args, "--completed"), out var c) ? c : 1;
        var pdfsDir = GetArg(args, "--pdfs-dir");

        var layout = NewLayout(root);
        Directory.CreateDirectory(root);

        var queue = NewQueue(root);
        var pdfHashes = new Dictionary<string, string>();

        for (var i = 1; i <= pending; i++)
        {
            var id = $"p103-job-pending-{i:D3}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
            queue.EnqueueAsync(MakeJob(id, $"Phase 10.3 pending job {i}", JobStatus.Pending)).GetAwaiter().GetResult();
            if (pdfsDir is not null)
            {
                // A real PDF-sized payload under the layout's queued dir (the
                // app's own storage layout), hashed for byte-identity checks.
                var queuedPath = Path.Combine(layout.JobsQueuedDir, id + ".pdf");
                Directory.CreateDirectory(Path.GetDirectoryName(queuedPath)!);
                var payload = new byte[128 * 1024];
                RandomNumberGenerator.Fill(payload);
                payload[0] = (byte)'%'; payload[1] = (byte)'P'; payload[2] = (byte)'D'; payload[3] = (byte)'F';
                File.WriteAllBytes(queuedPath, payload);
                pdfHashes[Path.GetFileName(queuedPath)] = Sha256File(queuedPath);
            }
        }

        for (var i = 1; i <= completed; i++)
        {
            var id = $"p103-job-printed-{i:D3}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
            queue.EnqueueAsync(MakeJob(id, $"Phase 10.3 completed job {i}", JobStatus.Printed)).GetAwaiter().GetResult();
        }

        var dbPath = layout.QueueDbPath;
        var snapshot = new
        {
            tool = "PrintEXE.QueueHarness",
            command = "seed",
            utc = DateTimeOffset.UtcNow.ToString("O"),
            dataRoot = root,
            databasePath = dbPath,
            databaseSizeBytes = new FileInfo(dbPath).Length,
            databaseHeader16Hex = Header16Hex(dbPath),
            keyPath = layout.QueueKeyPath,
            keySha256Fingerprint = KeyFingerprint(root),
            cipherVersion = SqlCipherInitializer.IsEncryptionAvailable ? "4.5.2 community" : "UNAVAILABLE",
            jobs = queue.ListAsync().GetAwaiter().GetResult()
                .Select(j => new { j.JobId, status = j.Status.ToString() })
                .OrderBy(j => j.JobId),
            pdfHashes,
        };
        Console.WriteLine(JsonSerializer.Serialize(snapshot,
            new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    /// <summary>
    /// STAGE 5: reopen the SAME data root after an MSI upgrade through the
    /// app's own classes and compare against the pre-upgrade snapshot:
    /// key fingerprint continuity, DB decryptability, job states, PDF
    /// byte-identity, integrity_check, no duplicates. Machine-readable JSON.
    /// </summary>
    private static int RunVerify(string root, string[] args)
    {
        var expectedPath = GetArg(args, "--expected") ?? throw new ArgumentException("--expected <snapshot.json> is required");
        var expected = JsonDocument.Parse(File.ReadAllText(expectedPath)).RootElement;

        var layout = NewLayout(root);
        var dbPath = layout.QueueDbPath;
        if (!File.Exists(dbPath)) throw new InvalidOperationException("queue.db missing after upgrade");

        var failures = new List<string>();

        // 1. DPAPI key continuity: same fingerprint as pre-upgrade.
        var fpNow = KeyFingerprint(root);
        var fpBefore = expected.GetProperty("keySha256Fingerprint").GetString();
        if (fpNow != fpBefore) failures.Add($"key fingerprint changed: {fpBefore} -> {fpNow}");

        // 2. Still encrypted (header must NOT be plain SQLite).
        var headerNow = Header16Hex(dbPath);
        var plainMagic = Convert.ToHexString("SQLite format 3\0"u8.ToArray());
        if (headerNow == plainMagic) failures.Add("queue.db became PLAIN SQLITE after upgrade");
        var headerBefore = expected.GetProperty("databaseHeader16Hex").GetString();
        if (headerBefore is not null && headerBefore != plainMagic && headerNow != headerBefore)
        {
            failures.Add($"encrypted header changed across upgrade: {headerBefore} -> {headerNow}");
        }

        // 3. Genuine decryption + job-state comparison through the app classes.
        var queue = NewQueue(root);
        var jobs = queue.ListAsync().GetAwaiter().GetResult().ToList();
        var expectedJobs = expected.GetProperty("jobs").EnumerateArray()
            .Select(j => (id: j.GetProperty("JobId").GetString() ?? j.GetProperty("jobId").GetString(),
                          status: (j.GetProperty("Status").GetString() ?? j.GetProperty("status").GetString())))
            .ToList();
        if (jobs.Count != expectedJobs.Count) failures.Add($"job count changed: {expectedJobs.Count} -> {jobs.Count} (duplicates or loss?)");
        foreach (var (id, status) in expectedJobs)
        {
            var now = jobs.FirstOrDefault(j => j.JobId == id);
            if (now is null) { failures.Add($"job {id} LOST across upgrade"); continue; }
            if (!string.Equals(now.Status.ToString(), status, StringComparison.OrdinalIgnoreCase))
                failures.Add($"job {id} status changed: {status} -> {now.Status}");
        }

        // 4. PDF byte-identity.
        if (expected.TryGetProperty("pdfHashes", out var pdfs))
        {
            foreach (var pdf in pdfs.EnumerateObject())
            {
                var path = Path.Combine(layout.JobsQueuedDir, pdf.Name);
                if (!File.Exists(path)) { failures.Add($"PDF {pdf.Name} lost across upgrade"); continue; }
                if (Sha256File(path) != pdf.Value.GetString()) failures.Add($"PDF {pdf.Name} bytes changed across upgrade");
            }
        }

        // 5. SQLCipher integrity check with the app's own key (never logged).
        string integrity;
        {
            var password = NewKeyStore(root).GetKeyForConnectionString();
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection(
                $"Data Source={dbPath};Password={password};Pooling=false");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA integrity_check;";
            integrity = cmd.ExecuteScalar()?.ToString() ?? "";
        }
        if (integrity != "ok") failures.Add($"integrity_check returned '{integrity}'");

        var verdict = new
        {
            tool = "PrintEXE.QueueHarness",
            command = "verify",
            utc = DateTimeOffset.UtcNow.ToString("O"),
            dataRoot = root,
            keyFingerprintContinuity = fpNow == fpBefore,
            stillEncrypted = headerNow != plainMagic,
            jobCountBefore = expectedJobs.Count,
            jobCountAfter = jobs.Count,
            integrityCheck = integrity,
            failureCount = failures.Count,
            failures,
        };
        Console.WriteLine(JsonSerializer.Serialize(verdict,
            new JsonSerializerOptions { WriteIndented = true }));
        return failures.Count == 0 ? 0 : 2;
    }

    /// <summary>Standalone integrity report for any harness data root.</summary>
    private static int RunIntegrity(string root)
    {
        var layout = NewLayout(root);
        var dbPath = layout.QueueDbPath;
        if (!File.Exists(dbPath)) throw new InvalidOperationException("queue.db missing");

        var password = NewKeyStore(root).GetKeyForConnectionString();
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={dbPath};Password={password};Pooling=false");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA integrity_check;";
        var integrity = cmd.ExecuteScalar()?.ToString() ?? "";

        var result = new
        {
            tool = "PrintEXE.QueueHarness",
            command = "integrity",
            utc = DateTimeOffset.UtcNow.ToString("O"),
            databasePath = dbPath,
            databaseSizeBytes = new FileInfo(dbPath).Length,
            header16Hex = Header16Hex(dbPath),
            integrityCheck = integrity,
        };
        Console.WriteLine(JsonSerializer.Serialize(result,
            new JsonSerializerOptions { WriteIndented = true }));
        return integrity == "ok" ? 0 : 2;
    }
}
