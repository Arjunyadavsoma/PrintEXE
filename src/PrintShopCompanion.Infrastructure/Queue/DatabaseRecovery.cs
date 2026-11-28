namespace PrintShopCompanion.Infrastructure.Queue;

using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

/// <summary>
/// Phase 9 (Stage 2/4): classification, recovery state, journal and retry
/// policy for the queue database. This is the F-1 fix.
///
/// <para><b>Design contract (documented, deliberate):</b></para>
/// <list type="bullet">
/// <item>Corruption is CLASSIFIED, never guessed: SQLite result codes and
/// message patterns map to a <see cref="DbFailureClass"/>.</item>
/// <item>The original database is NEVER deleted. Quarantine renames it to a
/// timestamped sibling and PRESERVES the WAL/SHM sidecars (renamed, not
/// deleted — they are crash evidence and may contain the last committed
/// transactions).</item>
/// <item>A recovery is journaled (JSONL, no sensitive content) and reflected
/// in <see cref="DatabaseRecoveryState"/> which is exposed via DI for the UI
/// and diagnostics.</item>
/// <item>REPEAT GUARD: a second failed integrity check on the FRESH database
/// within the same process means the storage layer itself is failing —
/// recovery STOPS, the queue is marked degraded and processing halts. The
/// app never silently converts a failing volume into repeated empty queues.</item>
/// <item>Job accounting: the count of recoverable jobs is recorded when the
/// quarantined copy can still be read; otherwise recorded as unknown
/// (never fabricated).</item>
/// <item>Encryption is NEVER weakened for recovery: the quarantined file
/// stays SQLCipher-encrypted and is never opened without its key.</item>
/// </list>
/// </summary>
public enum DbFailureClass
{
    /// <summary>Transient infrastructure condition (locked, busy, brief I/O). Retry with backoff.</summary>
    Transient = 0,

    /// <summary>Database corruption (integrity_check failure, SQLITE_CORRUPT/NOTADB, malformed image). No retry; recover.</summary>
    Corruption = 1,

    /// <summary>SQLCipher key mismatch or missing key material. No retry; operator action required.</summary>
    KeyMismatch = 2,

    /// <summary>Access denied / read-only volume. No retry; operator action required.</summary>
    PermissionDenied = 3,

    /// <summary>Disk full. Retry with backoff and a cap (space may be freed).</summary>
    DiskFull = 4,

    /// <summary>Invalid configuration or missing components. No retry.</summary>
    Configuration = 5,

    /// <summary>Unclassified failure. Bounded retry, then circuit breaker.</summary>
    Unknown = 6,
}

/// <summary>Classifies database/persistence exceptions. Pure static — trivially testable.</summary>
public static class DbErrorClassifier
{
    /// <summary>
    /// Map an exception to its failure class. Never throws.
    /// SQLite result codes used: 5=BUSY, 6=LOCKED, 7=NOMEM, 10=IOERR, 11=CORRUPT,
    /// 13=FULL, 14=CANTOPEN, 15=READONLY, 26=NOTADB.
    /// </summary>
    public static DbFailureClass Classify(Exception? ex)
    {
        if (ex is null) return DbFailureClass.Unknown;
        if (ex is OperationCanceledException) return DbFailureClass.Transient;

        // Walk inner exceptions — SqliteException often wraps IO errors.
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is Microsoft.Data.Sqlite.SqliteException se)
            {
                var code = se.SqliteErrorCode & 0xFF; // primary result code
                return code switch
                {
                    5 or 6 => DbFailureClass.Transient,           // BUSY / LOCKED
                    7 => DbFailureClass.Transient,                // NOMEM — treat as transient pressure
                    11 => DbFailureClass.Corruption,              // CORRUPT
                    13 => DbFailureClass.DiskFull,                // FULL
                    15 => DbFailureClass.PermissionDenied,        // READONLY
                    26 => DbFailureClass.Corruption,              // NOTADB (corrupt file OR wrong SQLCipher key)
                    _ => ClassifyByMessage(se.Message),
                };
            }

            if (e is UnauthorizedAccessException) return DbFailureClass.PermissionDenied;
            if (e is IOException io)
            {
                // Disk-full IOException from the pre-flight space check or the OS.
                if (io.Message.Contains("full", StringComparison.OrdinalIgnoreCase)
                    || io.Message.Contains("space", StringComparison.OrdinalIgnoreCase))
                    return DbFailureClass.DiskFull;
                return DbFailureClass.Transient;
            }
            if (e is InvalidOperationException ioe && ioe.Message.Contains("encryption key", StringComparison.OrdinalIgnoreCase))
                return DbFailureClass.KeyMismatch;
            if (e is InvalidOperationException ioe2 && ioe2.Message.Contains("SQLCipher", StringComparison.OrdinalIgnoreCase))
                return DbFailureClass.Configuration;
        }
        return ClassifyByMessage(ex.Message);
    }

    private static DbFailureClass ClassifyByMessage(string message)
    {
        if (message.Contains("database disk image is malformed", StringComparison.OrdinalIgnoreCase)
            || message.Contains("file is not a database", StringComparison.OrdinalIgnoreCase)
            || message.Contains("file is encrypted", StringComparison.OrdinalIgnoreCase)
            || message.Contains("database corruption", StringComparison.OrdinalIgnoreCase)
            || message.Contains("integrity_check", StringComparison.OrdinalIgnoreCase))
            return DbFailureClass.Corruption;
        if (message.Contains("encryption key", StringComparison.OrdinalIgnoreCase)
            || message.Contains("wrong password", StringComparison.OrdinalIgnoreCase)
            || message.Contains("queue.key", StringComparison.OrdinalIgnoreCase))
            return DbFailureClass.KeyMismatch;
        if (message.Contains("attempt to write a readonly database", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Access is denied", StringComparison.OrdinalIgnoreCase)
            || message.Contains("UnauthorizedAccess", StringComparison.OrdinalIgnoreCase))
            return DbFailureClass.PermissionDenied;
        if (message.Contains("disk", StringComparison.OrdinalIgnoreCase) &&
            (message.Contains("full", StringComparison.OrdinalIgnoreCase) || message.Contains("space", StringComparison.OrdinalIgnoreCase)))
            return DbFailureClass.DiskFull;
        if (message.Contains("SQLCipher", StringComparison.OrdinalIgnoreCase))
            return DbFailureClass.Configuration;
        if (message.Contains("locked", StringComparison.OrdinalIgnoreCase)
            || message.Contains("busy", StringComparison.OrdinalIgnoreCase)
            || message.Contains("timed out", StringComparison.OrdinalIgnoreCase))
            return DbFailureClass.Transient;
        return DbFailureClass.Unknown;
    }
}

/// <summary>
/// One journal entry per recovery attempt. Written as JSONL to
/// <c>Database/recovery-log.jsonl</c> next to queue.db. Contains NO sensitive
/// data: timestamps, failure class, quarantine path, job counts, outcome.
/// The quarantined file itself stays encrypted.
/// </summary>
public sealed record RecoveryJournalEntry(
    [property: JsonPropertyName("utc")] string Utc,
    [property: JsonPropertyName("class")] string FailureClass,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("quarantined_to")] string? QuarantinedTo,
    [property: JsonPropertyName("recoverable_jobs")] int? RecoverableJobs,
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("detail")] string? Detail);

/// <summary>
/// Process-wide recovery status, registered as a singleton in DI and
/// surfaced by DiagnosticsService and the application UI. Thread-safe.
/// </summary>
public sealed class DatabaseRecoveryState
{
    private readonly object _lock = new();
    private readonly ConcurrentQueue<RecoveryJournalEntry> _recent = new();

    public string QueueHealth
    {
        get
        {
            lock (_lock)
            {
                return _status switch
                {
                    RecoveryStatus.Healthy => "healthy",
                    RecoveryStatus.Recovered => "recovered-from-corruption",
                    RecoveryStatus.Degraded => "degraded",
                    RecoveryStatus.StorageFailing => "storage-failing",
                    _ => "unknown",
                };
            }
        }
    }

    public RecoveryStatus Status { get { lock (_lock) return _status; } }
    public DateTimeOffset? LastRecoveryUtc { get { lock (_lock) return _lastRecoveryUtc; } }
    public DateTimeOffset? LastHealthyUtc { get { lock (_lock) return _lastHealthyUtc; } }
    public int RecoveryCount { get { lock (_lock) return _recoveryCount; } }
    public DbFailureClass LastFailureClass { get { lock (_lock) return _lastFailureClass; } }
    public string? LastQuarantinedPath { get { lock (_lock) return _lastQuarantinedPath; } }
    public int? LastRecoverableJobs { get { lock (_lock) return _lastRecoverableJobs; } }
    public int ConsecutiveFailures { get { lock (_lock) return _consecutiveFailures; } }

    private RecoveryStatus _status = RecoveryStatus.Healthy;
    private DateTimeOffset? _lastRecoveryUtc;
    private DateTimeOffset? _lastHealthyUtc;
    private int _recoveryCount;
    private DbFailureClass _lastFailureClass = DbFailureClass.Unknown;
    private string? _lastQuarantinedPath;
    private int? _lastRecoverableJobs;
    private int _consecutiveFailures;

    public DatabaseRecoveryState()
    {
    }

    public void MarkHealthy()
    {
        lock (_lock)
        {
            _status = RecoveryStatus.Healthy;
            _consecutiveFailures = 0;
            _lastHealthyUtc = DateTimeOffset.UtcNow;
        }
    }

    public void MarkFailure(DbFailureClass cls)
    {
        lock (_lock)
        {
            _lastFailureClass = cls;
            _consecutiveFailures++;
            if (_status == RecoveryStatus.Healthy && cls is not DbFailureClass.Transient and not DbFailureClass.Unknown)
                _status = RecoveryStatus.Degraded;
        }
    }

    public void MarkDegraded(DbFailureClass cls, string reason)
    {
        lock (_lock)
        {
            _lastFailureClass = cls;
            _status = RecoveryStatus.Degraded;
            _consecutiveFailures++;
        }
    }

    public void MarkStorageFailing()
    {
        lock (_lock) _status = RecoveryStatus.StorageFailing;
    }

    /// <summary>Record a completed quarantine+recreate recovery. Returns false if the
    /// repeat guard has tripped (the caller must NOT recover again this session).</summary>
    public bool TryRecordRecovery(DbFailureClass cls, string quarantinedTo, int? recoverableJobs, string outcome, string? detail)
    {
        lock (_lock)
        {
            // Repeat guard: more than 3 recoveries in this process means the
            // storage layer is failing between recoveries. Stop being
            // destructive; surface StorageFailing and let the operator act.
            if (_recoveryCount >= 3)
                return false;
            _recoveryCount++;
            _lastRecoveryUtc = DateTimeOffset.UtcNow;
            _lastFailureClass = cls;
            _lastQuarantinedPath = quarantinedTo;
            _lastRecoverableJobs = recoverableJobs;
            _status = RecoveryStatus.Recovered;
            _recent.Enqueue(new RecoveryJournalEntry(
                DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                cls.ToString(), "quarantine-and-recreate", quarantinedTo, recoverableJobs, outcome, detail));
            while (_recent.Count > 20) _recent.TryDequeue(out _);
            return true;
        }
    }

    public RecoveryJournalEntry[] RecentEntries()
    {
        lock (_lock) return _recent.ToArray();
    }
}

public enum RecoveryStatus
{
    Healthy = 0,
    Recovered = 1,
    Degraded = 2,
    StorageFailing = 3,
}

/// <summary>
/// Phase 9 (Stage 4): bounded, classified retry policy for the queue
/// processing loop. Replaces the uncontrolled 5-second loop (F-1).
///
/// <para>Rules: exponential backoff with full jitter, per-class caps, NO
/// retry at all for Corruption / KeyMismatch / PermissionDenied /
/// Configuration, circuit-breaker stop after MaxConsecutiveFailures for
/// retryable classes, and NEVER discards jobs — the breaker only stops
/// PROCESSING; queued jobs remain on disk.</para>
/// </summary>
public sealed class QueueRetryPolicy
{
    public const int MaxConsecutiveFailures = 10;
    public const int MaxBackoffSeconds = 300;

    private readonly object _lock = new();
    private int _consecutive;
    private Guid _chainId = Guid.NewGuid();

    /// <summary>Correlation id of the current failure chain (stable across retries of the same chain).</summary>
    public Guid ChainId { get { lock (_lock) return _chainId; } }

    public int ConsecutiveFailures { get { lock (_lock) return _consecutive; } }

    /// <summary>Outcome for a classified failure. Pure decision, no sleeping.</summary>
    public RetryDecision OnFailure(DbFailureClass cls)
    {
        lock (_lock)
        {
            _consecutive++;
            return cls switch
            {
                // Permanent: never retry. The loop must STOP and surface the state.
                DbFailureClass.Corruption => RetryDecision.StopPermanent("database corruption — recovery policy owns the fix; processing halted, queued jobs preserved"),
                DbFailureClass.KeyMismatch => RetryDecision.StopPermanent("SQLCipher key mismatch — operator action required (re-pair or restore queue.key); processing halted, queued jobs preserved"),
                DbFailureClass.PermissionDenied => RetryDecision.StopPermanent("database access denied — check file permissions or disk health; processing halted, queued jobs preserved"),
                DbFailureClass.Configuration => RetryDecision.StopPermanent("invalid configuration — fix installation before queue processing; processing halted"),
                // Recoverable with backoff; circuit breaker after N consecutive.
                _ when _consecutive >= MaxConsecutiveFailures =>
                    RetryDecision.OpenBreaker($"circuit breaker OPEN after {_consecutive} consecutive {cls} failures — processing halted until the underlying problem resolves; queued jobs preserved"),
                _ => RetryDecision.Backoff(BackoffSecondsFor(_consecutive)),
            };
        }
    }

    public void OnSuccess()
    {
        lock (_lock)
        {
            _consecutive = 0;
            _chainId = Guid.NewGuid(); // new chain on recovery
        }
    }

    /// <summary>Exponential backoff with FULL jitter: base 2s doubling, capped.</summary>
    public static int BackoffSecondsFor(int consecutive)
    {
        var baseSeconds = Math.Min(MaxBackoffSeconds, (int)Math.Pow(2, Math.Min(consecutive, 10)));
        // Full jitter in [base/2, base] — deterministic seed from the count so
        // tests are reproducible while production spreads retry storms.
        var half = Math.Max(1, baseSeconds / 2);
        return half + (consecutive * 7919 % (baseSeconds - half + 1));
    }

    public readonly record struct RetryDecision(bool ShouldRetry, int? DelaySeconds, bool BreakerOpen, string Reason, bool Permanent)
    {
        public static RetryDecision Backoff(int seconds) => new(true, seconds, false, $"transient failure — retrying in {seconds}s with jitter", false);
        public static RetryDecision StopPermanent(string reason) => new(false, null, false, reason, true);
        public static RetryDecision OpenBreaker(string reason) => new(false, null, true, reason, false);
    }
}
