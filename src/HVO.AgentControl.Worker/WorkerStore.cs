using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HVO.AgentControl.Worker;

public sealed class WorkerStore : IDisposable, IWorkerObservationSink
{
    public const int SchemaVersion = 8;
    public const string SchemaSignature = "hvo-worker-bridge-v8-20260915";
    public const int ReplayGapLimit = 128;
    private static readonly string[] HoldNames = ["manual", "replay-gap", "replay-loss", "process", "permission", "ownership", "transport", "journal"];
    private const string ReplayLossMarker = "{\"loss\":\"events-dropped\"}";
    private const UnixFileMode PrivateDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private SqliteConnection _connection = null!;
    private readonly FileStream _instanceLock;
    private readonly WorkerOptions _options;
    private readonly IWorkerClock _clock;
    private readonly string _databasePath;
    private readonly string _lockPath;
    private readonly object _databaseGate = new();
    private long _leaseDeadline;
    private int _journalFailClosed;
    private bool _disposed;

    public WorkerStore(WorkerOptions options, IWorkerClock? clock = null)
    {
        _options = options;
        _clock = clock ?? new SystemWorkerClock();
        WorkerProtocol.ValidateIdentifier(options.WorkerId, WorkerProtocol.MaxIdentifierLength, "worker id");
        WorkerProtocol.ValidateIdentifier(options.ControllerId, WorkerProtocol.MaxIdentifierLength, "controller id");
        ValidateControlDirectory(options.ControlDirectory, options.ExpectedBridgeUid);
        _databasePath = Path.Combine(options.ControlDirectory, "bridge.db");
        _lockPath = Path.Combine(options.ControlDirectory, "bridge.lock");
        CleanOwnedDatabaseTemps(options.ControlDirectory, options.ExpectedBridgeUid);
        _instanceLock = AcquireInstanceLock(_lockPath, options.ExpectedBridgeUid);

        try
        {
            var existed = File.Exists(_databasePath);
            ValidateAbsentOrPrivateRegular(_databasePath, allowAbsent: true, expectedUid: options.ExpectedBridgeUid);
            ValidateAbsentOrPrivateRegular(_databasePath + "-wal", allowAbsent: true, expectedUid: options.ExpectedBridgeUid);
            ValidateAbsentOrPrivateRegular(_databasePath + "-shm", allowAbsent: true, expectedUid: options.ExpectedBridgeUid);
            if (existed)
            {
                _connection = OpenDatabase(_databasePath, create: false);
                ConfigureConnection();
                ValidateSchema();
            }
            else
            {
                _connection = CreateAndPublishDatabase();
            }
            SecureDatabaseFiles();
            IncrementWorkerGenerationAndReconcileStartup();
        }
        catch
        {
            _instanceLock.Dispose();
            throw;
        }
    }

    public long WorkerGeneration { get { lock (_databaseGate) return MetaLongLocked("worker_generation"); } }
    public long ProcessGeneration { get { lock (_databaseGate) return MetaLongLocked("process_generation"); } }

    private static SqliteConnection OpenDatabase(string path, bool create)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
            Pooling = false,
            DefaultTimeout = 1,
        }.ToString());
        try { connection.Open(); return connection; }
        catch (SqliteException exception) { connection.Dispose(); throw new WorkerStoreException("Worker journal could not be opened safely.", exception); }
    }

    private SqliteConnection CreateAndPublishDatabase()
    {
        var temporary = Path.Combine(_options.ControlDirectory, $".bridge.db.{Guid.NewGuid():N}.tmp");
        SqliteConnection? connection = null;
        try
        {
            connection = OpenDatabase(temporary, create: true);
            _connection = connection;
            ConfigureConnection();
            CreateSchema();
            Execute("PRAGMA wal_checkpoint(TRUNCATE)");
            connection.Close(); connection.Dispose(); connection = null;
            foreach (var sidecar in new[] { temporary + "-wal", temporary + "-shm" }) try { File.Delete(sidecar); } catch { }
            ValidateAbsentOrPrivateRegular(temporary, allowAbsent: false, expectedUid: _options.ExpectedBridgeUid, requireMode: false);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, PrivateFileMode);
            File.Move(temporary, _databasePath, false);
            FsyncDirectory(_options.ControlDirectory);
            var published = OpenDatabase(_databasePath, create: false);
            _connection = published;
            ConfigureConnection();
            ValidateSchema();
            return published;
        }
        catch
        {
            connection?.Dispose();
            foreach (var path in new[] { temporary, temporary + "-wal", temporary + "-shm" }) try { File.Delete(path); } catch { }
            throw;
        }
    }

    private void ConfigureConnection() => Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=1000;");

    private void CreateSchema()
    {
        using var tx = _connection.BeginTransaction();
        Execute("""
            CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE lease(singleton INTEGER PRIMARY KEY CHECK(singleton=1), epoch INTEGER NOT NULL CHECK(epoch>=0), controller_id TEXT, connection_nonce TEXT, observed_utc TEXT, active INTEGER NOT NULL CHECK(active IN(0,1)));
            CREATE TABLE requests(request_id TEXT PRIMARY KEY, payload_hash TEXT NOT NULL, state TEXT NOT NULL CHECK(state IN('forwarding','forwarded','completed','failed','uncertain')), outcome_json TEXT, process_generation INTEGER NOT NULL CHECK(process_generation>=0), ownership_epoch INTEGER NOT NULL CHECK(ownership_epoch>0), turn_id TEXT NOT NULL, session_id TEXT NOT NULL, created_utc TEXT NOT NULL);
            CREATE TABLE cancellations(cancellation_id TEXT PRIMARY KEY, target_request_id TEXT NOT NULL, payload_hash TEXT NOT NULL, state TEXT NOT NULL CHECK(state IN('forwarding','forwarded','uncertain')), process_generation INTEGER NOT NULL CHECK(process_generation>=0), ownership_epoch INTEGER NOT NULL CHECK(ownership_epoch>0), created_utc TEXT NOT NULL);
            CREATE TABLE event_generations(worker_generation INTEGER PRIMARY KEY CHECK(worker_generation>0), last_sequence INTEGER NOT NULL CHECK(last_sequence>=0));
            CREATE TABLE events(worker_generation INTEGER NOT NULL CHECK(worker_generation>0), sequence INTEGER NOT NULL CHECK(sequence>0), kind TEXT NOT NULL, payload_json TEXT NOT NULL, byte_count INTEGER NOT NULL CHECK(byte_count>=0), created_utc TEXT NOT NULL, PRIMARY KEY(worker_generation, sequence), FOREIGN KEY(worker_generation) REFERENCES event_generations(worker_generation));
            CREATE TABLE replay(singleton INTEGER PRIMARY KEY CHECK(singleton=1), ack_worker_generation INTEGER NOT NULL CHECK(ack_worker_generation>=0), ack_sequence INTEGER NOT NULL CHECK(ack_sequence>=0));
            CREATE TABLE replay_loss(worker_generation INTEGER PRIMARY KEY CHECK(worker_generation>0), marker_sequence INTEGER NOT NULL CHECK(marker_sequence>0), dropped_count INTEGER NOT NULL CHECK(dropped_count>0), dropped_bytes INTEGER NOT NULL CHECK(dropped_bytes>=0), reconciled INTEGER NOT NULL CHECK(reconciled IN(0,1)), FOREIGN KEY(worker_generation) REFERENCES event_generations(worker_generation));
            CREATE TABLE replay_gaps(id TEXT PRIMARY KEY, kind TEXT NOT NULL CHECK(kind IN('status','loss','overflow')), worker_generation INTEGER NOT NULL CHECK(worker_generation>=0), after_sequence INTEGER NOT NULL CHECK(after_sequence>=0), first_retained INTEGER NOT NULL CHECK(first_retained>=0), last_sequence INTEGER NOT NULL CHECK(last_sequence>=0), loss_marker_generation INTEGER, loss_marker_sequence INTEGER, reconciled INTEGER NOT NULL CHECK(reconciled IN(0,1)), CHECK((kind='loss' AND loss_marker_generation IS NOT NULL AND loss_marker_sequence IS NOT NULL) OR (kind<>'loss' AND loss_marker_generation IS NULL AND loss_marker_sequence IS NULL)), UNIQUE(kind,worker_generation,after_sequence,first_retained,last_sequence,loss_marker_generation,loss_marker_sequence));
            CREATE TABLE journal_failures(operation_id TEXT PRIMARY KEY, worker_generation INTEGER NOT NULL CHECK(worker_generation>0), error_category TEXT NOT NULL, reconciled INTEGER NOT NULL CHECK(reconciled IN(0,1)), created_utc TEXT NOT NULL);
            CREATE TABLE pending_permissions(decision_id TEXT PRIMARY KEY, process_generation INTEGER NOT NULL CHECK(process_generation>=0), ownership_epoch INTEGER NOT NULL CHECK(ownership_epoch>0), request_id TEXT NOT NULL, turn_id TEXT NOT NULL, payload_hash TEXT NOT NULL, option_ids_json TEXT NOT NULL, byte_count INTEGER NOT NULL CHECK(byte_count>=0), state TEXT NOT NULL CHECK(state IN('pending','deciding','decided','uncertain','invalidated')), decision TEXT, created_utc TEXT NOT NULL);
            CREATE TABLE process_slot(singleton INTEGER PRIMARY KEY CHECK(singleton=1), state TEXT NOT NULL CHECK(state IN('stopped','starting','running','exited','protocol-failed','transport-uncertain')), lifecycle_handle TEXT, active_request_id TEXT, session_id TEXT, pid INTEGER, updated_utc TEXT NOT NULL);
            CREATE TABLE holds(name TEXT PRIMARY KEY, held INTEGER NOT NULL CHECK(held IN(0,1)), reason TEXT);
            CREATE INDEX ix_pending_permissions_state ON pending_permissions(state, created_utc);
            """, tx);
        InsertMeta("schema_version", SchemaVersion.ToString(CultureInfo.InvariantCulture), tx);
        InsertMeta("schema_signature", SchemaSignature, tx);
        InsertMeta("worker_id", _options.WorkerId, tx);
        InsertMeta("controller_id", _options.ControllerId, tx);
        InsertMeta("worker_generation", "0", tx);
        InsertMeta("process_generation", "0", tx);
        InsertMeta("next_event_sequence", "1", tx);
        Execute("INSERT INTO lease VALUES(1,0,NULL,NULL,NULL,0); INSERT INTO replay VALUES(1,0,0); INSERT INTO process_slot VALUES(1,'stopped',NULL,NULL,NULL,NULL,$now); INSERT INTO holds VALUES('manual',0,NULL),('replay-gap',0,NULL),('replay-loss',0,NULL),('process',0,NULL),('permission',0,NULL),('ownership',0,NULL),('transport',0,NULL),('journal',0,NULL);", tx, ("$now", Now()));
        tx.Commit();
    }

    private void ValidateSchema()
    {
        try
        {
            var expected = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["index:ix_pending_permissions_state"] = "CREATE INDEX ix_pending_permissions_state ON pending_permissions(state, created_utc)",
                ["table:cancellations"] = "CREATE TABLE cancellations(cancellation_id TEXT PRIMARY KEY, target_request_id TEXT NOT NULL, payload_hash TEXT NOT NULL, state TEXT NOT NULL CHECK(state IN('forwarding','forwarded','uncertain')), process_generation INTEGER NOT NULL CHECK(process_generation>=0), ownership_epoch INTEGER NOT NULL CHECK(ownership_epoch>0), created_utc TEXT NOT NULL)",
                ["table:event_generations"] = "CREATE TABLE event_generations(worker_generation INTEGER PRIMARY KEY CHECK(worker_generation>0), last_sequence INTEGER NOT NULL CHECK(last_sequence>=0))",
                ["table:events"] = "CREATE TABLE events(worker_generation INTEGER NOT NULL CHECK(worker_generation>0), sequence INTEGER NOT NULL CHECK(sequence>0), kind TEXT NOT NULL, payload_json TEXT NOT NULL, byte_count INTEGER NOT NULL CHECK(byte_count>=0), created_utc TEXT NOT NULL, PRIMARY KEY(worker_generation, sequence), FOREIGN KEY(worker_generation) REFERENCES event_generations(worker_generation))",
                ["table:holds"] = "CREATE TABLE holds(name TEXT PRIMARY KEY, held INTEGER NOT NULL CHECK(held IN(0,1)), reason TEXT)",
                ["table:journal_failures"] = "CREATE TABLE journal_failures(operation_id TEXT PRIMARY KEY, worker_generation INTEGER NOT NULL CHECK(worker_generation>0), error_category TEXT NOT NULL, reconciled INTEGER NOT NULL CHECK(reconciled IN(0,1)), created_utc TEXT NOT NULL)",
                ["table:lease"] = "CREATE TABLE lease(singleton INTEGER PRIMARY KEY CHECK(singleton=1), epoch INTEGER NOT NULL CHECK(epoch>=0), controller_id TEXT, connection_nonce TEXT, observed_utc TEXT, active INTEGER NOT NULL CHECK(active IN(0,1)))",
                ["table:meta"] = "CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT NOT NULL)",
                ["table:pending_permissions"] = "CREATE TABLE pending_permissions(decision_id TEXT PRIMARY KEY, process_generation INTEGER NOT NULL CHECK(process_generation>=0), ownership_epoch INTEGER NOT NULL CHECK(ownership_epoch>0), request_id TEXT NOT NULL, turn_id TEXT NOT NULL, payload_hash TEXT NOT NULL, option_ids_json TEXT NOT NULL, byte_count INTEGER NOT NULL CHECK(byte_count>=0), state TEXT NOT NULL CHECK(state IN('pending','deciding','decided','uncertain','invalidated')), decision TEXT, created_utc TEXT NOT NULL)",
                ["table:process_slot"] = "CREATE TABLE process_slot(singleton INTEGER PRIMARY KEY CHECK(singleton=1), state TEXT NOT NULL CHECK(state IN('stopped','starting','running','exited','protocol-failed','transport-uncertain')), lifecycle_handle TEXT, active_request_id TEXT, session_id TEXT, pid INTEGER, updated_utc TEXT NOT NULL)",
                ["table:replay"] = "CREATE TABLE replay(singleton INTEGER PRIMARY KEY CHECK(singleton=1), ack_worker_generation INTEGER NOT NULL CHECK(ack_worker_generation>=0), ack_sequence INTEGER NOT NULL CHECK(ack_sequence>=0))",
                ["table:replay_gaps"] = "CREATE TABLE replay_gaps(id TEXT PRIMARY KEY, kind TEXT NOT NULL CHECK(kind IN('status','loss','overflow')), worker_generation INTEGER NOT NULL CHECK(worker_generation>=0), after_sequence INTEGER NOT NULL CHECK(after_sequence>=0), first_retained INTEGER NOT NULL CHECK(first_retained>=0), last_sequence INTEGER NOT NULL CHECK(last_sequence>=0), loss_marker_generation INTEGER, loss_marker_sequence INTEGER, reconciled INTEGER NOT NULL CHECK(reconciled IN(0,1)), CHECK((kind='loss' AND loss_marker_generation IS NOT NULL AND loss_marker_sequence IS NOT NULL) OR (kind<>'loss' AND loss_marker_generation IS NULL AND loss_marker_sequence IS NULL)), UNIQUE(kind,worker_generation,after_sequence,first_retained,last_sequence,loss_marker_generation,loss_marker_sequence))",
                ["table:replay_loss"] = "CREATE TABLE replay_loss(worker_generation INTEGER PRIMARY KEY CHECK(worker_generation>0), marker_sequence INTEGER NOT NULL CHECK(marker_sequence>0), dropped_count INTEGER NOT NULL CHECK(dropped_count>0), dropped_bytes INTEGER NOT NULL CHECK(dropped_bytes>=0), reconciled INTEGER NOT NULL CHECK(reconciled IN(0,1)), FOREIGN KEY(worker_generation) REFERENCES event_generations(worker_generation))",
                ["table:requests"] = "CREATE TABLE requests(request_id TEXT PRIMARY KEY, payload_hash TEXT NOT NULL, state TEXT NOT NULL CHECK(state IN('forwarding','forwarded','completed','failed','uncertain')), outcome_json TEXT, process_generation INTEGER NOT NULL CHECK(process_generation>=0), ownership_epoch INTEGER NOT NULL CHECK(ownership_epoch>0), turn_id TEXT NOT NULL, session_id TEXT NOT NULL, created_utc TEXT NOT NULL)",
            };
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT type,name,sql FROM sqlite_master WHERE name NOT LIKE 'sqlite_autoindex_%' AND name NOT LIKE 'sqlite_%' ORDER BY type,name";
            using var reader = command.ExecuteReader();
            var actual = new Dictionary<string, string>(StringComparer.Ordinal);
            while (reader.Read()) actual[$"{reader.GetString(0)}:{reader.GetString(1)}"] = reader.GetString(2);
            if (actual.Count != expected.Count || expected.Any(pair => !actual.TryGetValue(pair.Key, out var sql) || !string.Equals(sql, pair.Value, StringComparison.Ordinal)))
                throw new WorkerStoreException("Worker journal schema does not exactly match the supported signature.");
            if (MetaLongLocked("schema_version") != SchemaVersion || MetaLocked("schema_signature") != SchemaSignature || MetaLocked("worker_id") != _options.WorkerId || MetaLocked("controller_id") != _options.ControllerId)
                throw new WorkerStoreException("Worker journal identity or schema signature mismatch.");
            using var holds = _connection.CreateCommand(); holds.CommandText = "SELECT name FROM holds ORDER BY name";
            using var holdReader = holds.ExecuteReader(); var holdNames = new List<string>(); while (holdReader.Read()) holdNames.Add(holdReader.GetString(0));
            if (!holdNames.SequenceEqual(HoldNames.Order(StringComparer.Ordinal), StringComparer.Ordinal)) throw new WorkerStoreException("Worker journal hold state does not exactly match the supported signature.");
            if (!string.Equals(Scalar("PRAGMA quick_check")?.ToString(), "ok", StringComparison.Ordinal)) throw new WorkerStoreException("Worker journal integrity check failed.");
            using var foreign = _connection.CreateCommand(); foreign.CommandText = "PRAGMA foreign_key_check";
            using var violations = foreign.ExecuteReader(); if (violations.Read()) throw new WorkerStoreException("Worker journal foreign-key validation failed.");
        }
        catch (SqliteException exception) { throw new WorkerStoreException("Worker journal validation failed closed.", exception); }
    }

    public Lease AcquireLease(string controllerId, string connectionNonce)
    {
        lock (_databaseGate) return AcquireLeaseLocked(controllerId, connectionNonce);
    }

    private Lease AcquireLeaseLocked(string controllerId, string connectionNonce)
    {
        WorkerProtocol.ValidateIdentifier(controllerId, WorkerProtocol.MaxIdentifierLength, "controller id");
        using var nonceBytes = new ZeroingBuffer(WorkerProtocol.ParseNonce(connectionNonce, "connection nonce"));
        if (controllerId != _options.ControllerId) throw new WorkerProtocolException("Controller identity is not enrolled.");
        using var tx = _connection.BeginTransaction();
        Execute("UPDATE lease SET epoch=epoch+1,controller_id=$c,connection_nonce=$n,observed_utc=$u,active=1 WHERE singleton=1; UPDATE holds SET held=0,reason=NULL WHERE name='ownership' AND reason='lease-expired'; UPDATE holds SET held=1,reason='ownership-changed-pending-permission' WHERE name='ownership' AND EXISTS(SELECT 1 FROM pending_permissions WHERE state IN('pending','deciding','uncertain'))", tx, ("$c", controllerId), ("$n", connectionNonce), ("$u", Now()));
        var epoch = Convert.ToInt64(Scalar("SELECT epoch FROM lease WHERE singleton=1", tx), CultureInfo.InvariantCulture);
        tx.Commit();
        _leaseDeadline = checked(_clock.MonotonicMilliseconds + (long)_options.LeaseLifetime.TotalMilliseconds);
        return new Lease(epoch, controllerId, connectionNonce, _clock.UtcNow);
    }

    public void RequireLease(long epoch, string nonce)
    {
        lock (_databaseGate) RequireLeaseLocked(epoch, nonce);
    }

    /// <summary>
    /// Checks whether an authenticated controller socket still owns the live lease
    /// without extending it or changing any hold state.
    /// </summary>
    public bool IsLeaseCurrent(long epoch, string nonce)
    {
        lock (_databaseGate)
        {
            if (epoch < 1) return false;
            byte[] parsed;
            try { parsed = WorkerProtocol.ParseNonce(nonce, "connection nonce"); }
            catch (WorkerProtocolException) { return false; }
            using var nonceBytes = new ZeroingBuffer(parsed);
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT epoch,connection_nonce,active FROM lease WHERE singleton=1";
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return false;
            return reader.GetInt64(0) == epoch
                && !reader.IsDBNull(1)
                && reader.GetString(1) == nonce
                && reader.GetInt64(2) == 1
                && _clock.MonotonicMilliseconds <= _leaseDeadline;
        }
    }

    private void RequireLeaseLocked(long epoch, string nonce)
    {
        if (epoch < 1) throw new WorkerProtocolException("Invalid ownership epoch.");
        using var nonceBytes = new ZeroingBuffer(WorkerProtocol.ParseNonce(nonce, "connection nonce"));
        using var command = _connection.CreateCommand(); command.CommandText = "SELECT epoch,connection_nonce,active FROM lease WHERE singleton=1";
        using var reader = command.ExecuteReader(); reader.Read();
        var identityMatches = reader.GetInt64(0) == epoch && !reader.IsDBNull(1) && reader.GetString(1) == nonce && reader.GetInt64(2) == 1;
        var expired = _clock.MonotonicMilliseconds > _leaseDeadline;
        reader.Close();
        if (!identityMatches || expired)
        {
            if (identityMatches && expired) SetHoldInternal("ownership", true, "lease-expired");
            throw new WorkerProtocolException("The ownership lease is stale or expired.");
        }
    }

    public void Heartbeat(long epoch, string nonce)
    {
        lock (_databaseGate)
        {
            RequireLeaseLocked(epoch, nonce);
            _leaseDeadline = checked(_clock.MonotonicMilliseconds + (long)_options.LeaseLifetime.TotalMilliseconds);
            Execute("UPDATE lease SET observed_utc=$u WHERE singleton=1", null, ("$u", Now()));
        }
    }

    public long BeginProcessStart()
    {
        lock (_databaseGate) return BeginProcessStartLocked();
    }

    private long BeginProcessStartLocked()
    {
        using var tx = _connection.BeginTransaction();
        var state = Scalar("SELECT state FROM process_slot WHERE singleton=1", tx)?.ToString();
        if (state is "running" or "starting") throw new WorkerProtocolException("The worker process is already active.");
        Execute("UPDATE meta SET value=CAST(value AS INTEGER)+1 WHERE key='process_generation'; UPDATE pending_permissions SET state='invalidated' WHERE state IN('pending','deciding','uncertain'); UPDATE holds SET held=0,reason=NULL WHERE name IN('ownership','permission'); UPDATE process_slot SET state='starting',lifecycle_handle=NULL,active_request_id=NULL,session_id=NULL,pid=NULL,updated_utc=$u WHERE singleton=1", tx, ("$u", Now()));
        var generation = Convert.ToInt64(Scalar("SELECT value FROM meta WHERE key='process_generation'", tx), CultureInfo.InvariantCulture);
        tx.Commit(); return generation;
    }

    public long AdvanceProcessGeneration() => BeginProcessStart();
    public void CompleteProcessStart(string lifecycleHandle, long? pid)
    {
        lock (_databaseGate) CompleteProcessStartLocked(lifecycleHandle, pid);
    }

    private void CompleteProcessStartLocked(string lifecycleHandle, long? pid)
    {
        WorkerProtocol.ValidateIdentifier(lifecycleHandle, WorkerProtocol.MaxIdentifierLength, "lifecycle handle");
        using var tx = _connection.BeginTransaction();
        Execute("UPDATE process_slot SET state='running',lifecycle_handle=$h,pid=$p,updated_utc=$u WHERE singleton=1 AND state='starting'", tx, ("$h", lifecycleHandle), ("$p", pid), ("$u", Now()));
        if (Convert.ToInt64(Scalar("SELECT changes()", tx), CultureInfo.InvariantCulture) != 1) throw new WorkerProtocolException("Process start acknowledgment does not match a pending start.");
        Execute("UPDATE holds SET held=0,reason=NULL WHERE name IN('process','transport')", tx);
        tx.Commit();
    }
    public void FailProcessStart(bool uncertain) { lock (_databaseGate) Execute("UPDATE process_slot SET state=$s,updated_utc=$u WHERE singleton=1 AND state='starting'", null, ("$s", uncertain ? "transport-uncertain" : "stopped"), ("$u", Now())); }
    public void SetProcessFailure(string state, string holdReason) => RecordProcessTermination(state, holdReason);
    public void RecordProcessTermination(string state, string holdReason)
    {
        if (state is not ("stopped" or "exited" or "protocol-failed" or "transport-uncertain")) throw new WorkerProtocolException("Invalid terminal process state.");
        if (holdReason is { Length: > 128 }) throw new WorkerProtocolException("Hold reason is too long.");
        lock (_databaseGate)
        {
            using var tx = _connection.BeginTransaction();
            Execute("UPDATE process_slot SET state=$s,active_request_id=NULL,pid=NULL,updated_utc=$u WHERE singleton=1; UPDATE pending_permissions SET state='invalidated' WHERE state IN('pending','deciding','uncertain'); UPDATE holds SET held=0,reason=NULL WHERE name IN('ownership','permission'); UPDATE holds SET held=1,reason=$r WHERE name=$n", tx, ("$s", state), ("$u", Now()), ("$r", holdReason), ("$n", HoldNameForProcessFailure(holdReason)));
            tx.Commit();
        }
    }
    private static string HoldNameForProcessFailure(string reason) => reason is "acp-transport-uncertain" or "permission-binding-uncertain" ? "transport" : "process";
    internal void FailClosedJournalInMemory() => Volatile.Write(ref _journalFailClosed, 1);

    public JournalFailure SetJournalFailure(string errorCategory)
    {
        WorkerProtocol.ValidateIdentifier(errorCategory, 64, "journal failure category");
        lock (_databaseGate)
        {
            var existing = CurrentJournalFailureLocked();
            if (existing is not null) return existing;
            var operationId = "journal:" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            var generation = MetaLongLocked("worker_generation");
            using var tx = _connection.BeginTransaction();
            Execute("INSERT INTO journal_failures VALUES($id,$g,$c,0,$u); UPDATE holds SET held=1,reason='journal-failed' WHERE name='journal'", tx,
                ("$id", operationId), ("$g", generation), ("$c", errorCategory), ("$u", Now()));
            tx.Commit();
            return new JournalFailure(operationId, generation, errorCategory);
        }
    }

    public void ReconcileJournalFailure(string operationId, long workerGeneration)
    {
        WorkerProtocol.ValidateIdentifier(operationId, WorkerProtocol.MaxIdentifierLength, "journal failure operation id");
        lock (_databaseGate)
        {
            var marker = CurrentJournalFailureLocked();
            if (marker is null || marker.OperationId != operationId || marker.WorkerGeneration != workerGeneration)
                throw new WorkerProtocolException("Journal failure marker does not match the current recovery obligation.");
            ValidateSchema();
            var currentGeneration = MetaLongLocked("worker_generation");
            using (var tx = _connection.BeginTransaction())
            {
                var probeSequence = long.MaxValue;
                Execute("INSERT INTO events VALUES($g,$s,'journal-probe','{}',2,$u); DELETE FROM events WHERE worker_generation=$g AND sequence=$s", tx,
                    ("$g", currentGeneration), ("$s", probeSequence), ("$u", Now()));
                tx.Commit();
            }
            Execute("PRAGMA wal_checkpoint(TRUNCATE)");
            using var reconcile = _connection.BeginTransaction();
            Execute("UPDATE journal_failures SET reconciled=1 WHERE operation_id=$id AND worker_generation=$g AND reconciled=0", reconcile,
                ("$id", operationId), ("$g", workerGeneration));
            if (Convert.ToInt64(Scalar("SELECT changes()", reconcile), CultureInfo.InvariantCulture) != 1)
                throw new WorkerProtocolException("Journal failure marker changed before reconciliation.");
            Execute("UPDATE holds SET held=0,reason=NULL WHERE name='journal' AND NOT EXISTS(SELECT 1 FROM journal_failures WHERE reconciled=0)", reconcile);
            reconcile.Commit();
        }
    }

    public void SetProcess(string state, long? pid = null)
    {
        if (state is not ("stopped" or "starting" or "running" or "exited" or "protocol-failed" or "transport-uncertain")) throw new WorkerProtocolException("Invalid process state.");
        lock (_databaseGate) Execute("UPDATE process_slot SET state=$s,pid=$p,updated_utc=$u WHERE singleton=1", null, ("$s", state), ("$p", pid), ("$u", Now()));
    }
    public void SetActiveRequest(string? requestId) { lock (_databaseGate) Execute("UPDATE process_slot SET active_request_id=$r,updated_utc=$u WHERE singleton=1", null, ("$r", requestId), ("$u", Now())); }
    public void BindSession(string sessionId)
    {
        WorkerProtocol.ValidateIdentifier(sessionId, WorkerProtocol.MaxIdentifierLength, "session id");
        lock (_databaseGate)
        {
            using var tx = _connection.BeginTransaction();
            var currentValue = Scalar("SELECT session_id FROM process_slot WHERE singleton=1", tx);
            var current = currentValue is null or DBNull ? null : currentValue.ToString();
            if (current is not null && current != sessionId) throw new WorkerProtocolException("The running process is already bound to a different session.");
            Execute("UPDATE process_slot SET session_id=$s,updated_utc=$u WHERE singleton=1 AND state='running'", tx, ("$s", sessionId), ("$u", Now()));
            if (Convert.ToInt64(Scalar("SELECT changes()", tx), CultureInfo.InvariantCulture) != 1) throw new WorkerProtocolException("A running process is required before binding its session.");
            tx.Commit();
        }
    }
    /// <summary>
    /// Records that a viewer stop could not be confirmed, so a viewer may still be
    /// attached to the session.
    /// </summary>
    /// <remarks>
    /// This is deliberately not one of the fixed dispatch holds: an unconfirmed
    /// viewer teardown says nothing about the ACP process or about prompt safety,
    /// and blocking dispatch for it would be a false safety signal. It suppresses
    /// only viewer availability, which is exactly the surface in doubt, and it is
    /// cleared when a later attach succeeds, because the supervisor refuses a
    /// second viewer while the previous one is alive.
    /// </remarks>
    public void SetViewerHold(string reason)
    {
        WorkerProtocol.ValidateIdentifier(reason, 64, "viewer hold reason");
        lock (_databaseGate) Execute("INSERT INTO meta VALUES('viewer_hold',$r) ON CONFLICT(key) DO UPDATE SET value=$r", null, ("$r", reason));
    }

    public void ClearViewerHold()
    {
        lock (_databaseGate) Execute("DELETE FROM meta WHERE key='viewer_hold'");
    }

    public string? ViewerHoldReason()
    {
        lock (_databaseGate) return Scalar("SELECT value FROM meta WHERE key='viewer_hold'")?.ToString();
    }

    public bool ViewerSessionBound()
    {
        lock (_databaseGate)
        {
            using var q = _connection.CreateCommand();
            q.CommandText = "SELECT COUNT(*) FROM process_slot WHERE singleton=1 AND state='running' AND session_id IS NOT NULL";
            return Convert.ToInt64(q.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
        }
    }

    public string RequireViewerLease(long epoch, string connectionNonce, string sessionId)
    {
        lock (_databaseGate)
        {
            RequireLeaseLocked(epoch, connectionNonce);
            WorkerProtocol.ValidateIdentifier(sessionId, WorkerProtocol.MaxIdentifierLength, "session id");
            using var q = _connection.CreateCommand(); q.CommandText = "SELECT state,session_id FROM process_slot WHERE singleton=1"; using var r = q.ExecuteReader(); r.Read();
            if (r.GetString(0) != "running" || r.IsDBNull(1) || r.GetString(1) != sessionId) throw new WorkerProtocolException("Viewer session does not match the running process binding.");
            return sessionId;
        }
    }

    public StoredRequest RegisterGatedRequest(long epoch, string nonce, string id, JsonElement payload, string? turnId) =>
        RegisterAndBeginForwardingGated(epoch, nonce, id, payload, turnId);

    public StoredRequest RegisterAndBeginForwardingGated(long epoch, string nonce, string id, JsonElement payload, string? turnId)
    {
        lock (_databaseGate) return RegisterAndBeginForwardingGatedLocked(epoch, nonce, id, payload, turnId);
    }

    private StoredRequest RegisterAndBeginForwardingGatedLocked(long epoch, string nonce, string id, JsonElement payload, string? turnId)
    {
        RequireLeaseLocked(epoch, nonce);
        WorkerProtocol.ValidateIdentifier(id, WorkerProtocol.MaxIdentifierLength, "request id");
        if (turnId is null) throw new WorkerProtocolException("Prompt submissions require a host turn id.");
        WorkerProtocol.ValidateIdentifier(turnId, WorkerProtocol.MaxIdentifierLength, "turn id");
        var sessionId = RequirePromptSession(payload);
        var hash = Convert.ToHexString(WorkerProtocol.CanonicalPayloadHash(payload)).ToLowerInvariant();
        using var tx = _connection.BeginTransaction();
        var existing = QueryRequest(id, tx);
        if (existing is not null)
        {
            if (existing.PayloadHash != hash || existing.TurnId != turnId || existing.SessionId != sessionId) throw new WorkerProtocolException("Request id was reused with different authorization data.");
            tx.Commit(); return existing;
        }
        EnsureDispatchAllowed(tx, epoch, nonce);
        var generation = Convert.ToInt64(Scalar("SELECT value FROM meta WHERE key='process_generation'", tx), CultureInfo.InvariantCulture);
        Execute("INSERT INTO requests VALUES($id,$h,'forwarding',NULL,$g,$e,$t,$s,$u)", tx, ("$id", id), ("$h", hash), ("$g", generation), ("$e", epoch), ("$t", turnId), ("$s", sessionId), ("$u", Now()));
        tx.Commit(); return new StoredRequest(id, hash, "forwarding", null, generation, epoch, turnId, sessionId);
    }

    private void EnsureDispatchAllowed(SqliteTransaction tx, long epoch, string nonce)
    {
        using var command = _connection.CreateCommand(); command.Transaction = tx;
        command.CommandText = "SELECT l.epoch,l.connection_nonce,l.active,p.state,(SELECT COUNT(*) FROM holds WHERE held=1) FROM lease l,process_slot p WHERE l.singleton=1 AND p.singleton=1";
        using var reader = command.ExecuteReader(); reader.Read();
        if (reader.GetInt64(0) != epoch || reader.IsDBNull(1) || reader.GetString(1) != nonce || reader.GetInt64(2) != 1 || _clock.MonotonicMilliseconds > _leaseDeadline)
            throw new WorkerProtocolException("The ownership lease is stale or expired.");
        if (reader.GetInt64(4) != 0 || Volatile.Read(ref _journalFailClosed) != 0) throw new WorkerProtocolException("Dispatch is held.");
        if (reader.GetString(3) != "running") throw new WorkerProtocolException("The worker process is not running.");
    }

    public void MarkForwarding(string id) { lock (_databaseGate) Execute("UPDATE requests SET state='forwarding' WHERE request_id=$id AND state='forwarding'", null, ("$id", id)); }
    public void MarkForwarded(string id) { lock (_databaseGate) Execute("UPDATE requests SET state='forwarded' WHERE request_id=$id AND state='forwarding'", null, ("$id", id)); }
    public void MarkUncertain(string id) { lock (_databaseGate) Execute("UPDATE requests SET state='uncertain' WHERE request_id=$id AND state IN('forwarding','forwarded')", null, ("$id", id)); }
    public void CompleteRequest(string id, string state, string? outcomeJson) { lock (_databaseGate) Execute("UPDATE requests SET state=$s,outcome_json=$o WHERE request_id=$id AND state IN('forwarding','forwarded')", null, ("$id", id), ("$s", state), ("$o", outcomeJson)); }
    public StoredRequest? GetRequest(string id) { lock (_databaseGate) return QueryRequestLocked(id, null); }

    public StoredCancellation RegisterCancellation(long epoch, string nonce, string cancellationId, string targetRequestId, JsonElement envelope)
    {
        lock (_databaseGate)
        {
            RequireLeaseLocked(epoch, nonce);
            WorkerProtocol.ValidateIdentifier(cancellationId, WorkerProtocol.MaxIdentifierLength, "cancellation id");
            WorkerProtocol.ValidateIdentifier(targetRequestId, WorkerProtocol.MaxIdentifierLength, "target request id");
            var target = QueryRequest(targetRequestId, null) ?? throw new WorkerProtocolException("Cancellation target is unknown.");
            var hash = Convert.ToHexString(WorkerProtocol.CanonicalPayloadHash(envelope)).ToLowerInvariant();
            using var tx = _connection.BeginTransaction();
            var existing = QueryCancellation(cancellationId, tx);
            if (existing is not null)
            {
                if (existing.TargetRequestId != targetRequestId || existing.PayloadHash != hash)
                    throw new WorkerProtocolException("Cancellation id was reused with a different target or payload.");
                tx.Commit();
                return existing;
            }
            if (target.State is not ("forwarding" or "forwarded")) throw new WorkerProtocolException("Cancellation target is not active.");
            var currentGeneration = Convert.ToInt64(Scalar("SELECT value FROM meta WHERE key='process_generation'", tx), CultureInfo.InvariantCulture);
            var processState = Scalar("SELECT state FROM process_slot WHERE singleton=1", tx)?.ToString();
            if (target.ProcessGeneration != currentGeneration || processState != "running")
                throw new WorkerProtocolException("Cancellation target process is no longer running.");
            if (target.OwnershipEpoch != epoch) throw new WorkerProtocolException("Cancellation target belongs to a prior ownership epoch.");
            Execute("INSERT INTO cancellations VALUES($id,$target,$hash,'forwarding',$generation,$epoch,$utc)", tx,
                ("$id", cancellationId), ("$target", targetRequestId), ("$hash", hash),
                ("$generation", target.ProcessGeneration), ("$epoch", epoch), ("$utc", Now()));
            tx.Commit();
            return new StoredCancellation(cancellationId, targetRequestId, hash, "forwarding", target.ProcessGeneration, epoch);
        }
    }

    public void MarkCancellationForwarded(string id) { lock (_databaseGate) Execute("UPDATE cancellations SET state='forwarded' WHERE cancellation_id=$id AND state='forwarding'", null, ("$id", id)); }
    public void MarkCancellationUncertain(string id) { lock (_databaseGate) Execute("UPDATE cancellations SET state='uncertain' WHERE cancellation_id=$id AND state='forwarding'", null, ("$id", id)); }
    public StoredCancellation? GetCancellation(string id) { lock (_databaseGate) return QueryCancellationLocked(id, null); }

    public WorkerEvent AppendEvent(string kind, string payloadJson)
    {
        lock (_databaseGate) return AppendEventLocked(kind, payloadJson);
    }

    private WorkerEvent AppendEventLocked(string kind, string payloadJson)
    {
        WorkerProtocol.ValidateIdentifier(kind, 64, "event kind");
        var bytes = Encoding.UTF8.GetByteCount(payloadJson);
        using var tx = _connection.BeginTransaction();
        var generation = Convert.ToInt64(Scalar("SELECT value FROM meta WHERE key='worker_generation'", tx), CultureInfo.InvariantCulture);
        var ackGeneration = Convert.ToInt64(Scalar("SELECT ack_worker_generation FROM replay WHERE singleton=1", tx), CultureInfo.InvariantCulture);
        var ackSequence = Convert.ToInt64(Scalar("SELECT ack_sequence FROM replay WHERE singleton=1", tx), CultureInfo.InvariantCulture);
        Execute("DELETE FROM replay_loss WHERE (worker_generation<$g OR (worker_generation=$g AND marker_sequence<=$s)) AND reconciled=1; DELETE FROM events WHERE worker_generation<$g OR (worker_generation=$g AND sequence<=$s); DELETE FROM event_generations WHERE worker_generation<$g AND NOT EXISTS(SELECT 1 FROM events WHERE events.worker_generation=event_generations.worker_generation) AND NOT EXISTS(SELECT 1 FROM replay_loss WHERE replay_loss.worker_generation=event_generations.worker_generation)", tx, ("$g", ackGeneration), ("$s", ackSequence));
        var count = Convert.ToInt64(Scalar("SELECT COUNT(*) FROM events", tx), CultureInfo.InvariantCulture);
        var total = Convert.ToInt64(Scalar("SELECT COALESCE(SUM(byte_count),0) FROM events", tx), CultureInfo.InvariantCulture);
        var sequence = Convert.ToInt64(Scalar("SELECT value FROM meta WHERE key='next_event_sequence'", tx), CultureInfo.InvariantCulture);
        var eventFitsReplayPage = bytes <= 64 * 1024 && FitsReplayPage(new WorkerEvent(generation, sequence, kind, payloadJson, bytes));
        if (!eventFitsReplayPage || bytes > _options.EventByteLimit || count + 1 > _options.EventLimit || total + bytes > _options.EventByteLimit)
        {
            var loss = Scalar("SELECT marker_sequence FROM replay_loss WHERE worker_generation=$g", tx, ("$g", generation));
            if (loss is null)
            {
                var markerBytes = Encoding.UTF8.GetByteCount(ReplayLossMarker);
                long droppedCount = 1;
                long droppedBytes = bytes;
                while ((count + 1 > _options.EventLimit || total + markerBytes > _options.EventByteLimit) && count > 0)
                {
                    using var removed = _connection.CreateCommand();
                    removed.Transaction = tx;
                    removed.CommandText = "SELECT worker_generation,sequence,byte_count,kind FROM events ORDER BY CASE kind WHEN 'events-dropped' THEN 1 ELSE 0 END,worker_generation,sequence LIMIT 1";
                    using var reader = removed.ExecuteReader();
                    reader.Read();
                    var removedGeneration = reader.GetInt64(0);
                    var removedSequence = reader.GetInt64(1);
                    var removedBytes = reader.GetInt64(2);
                    var removedKind = reader.GetString(3);
                    reader.Close();
                    Execute("DELETE FROM events WHERE worker_generation=$g AND sequence=$s", tx, ("$g", removedGeneration), ("$s", removedSequence));
                    count--;
                    total -= removedBytes;
                    if (removedKind != "events-dropped")
                    {
                        droppedCount++;
                        droppedBytes += removedBytes;
                    }
                }
                if (_options.EventLimit > 0 && markerBytes <= _options.EventByteLimit)
                    Execute("INSERT INTO events VALUES($g,$s,'events-dropped',$p,$b,$u)", tx, ("$g", generation), ("$s", sequence), ("$p", ReplayLossMarker), ("$b", markerBytes), ("$u", Now()));
                Execute("INSERT INTO replay_loss VALUES($g,$s,$c,$b,0); UPDATE event_generations SET last_sequence=$s WHERE worker_generation=$g; UPDATE meta SET value=CAST(value AS INTEGER)+1 WHERE key='next_event_sequence'; UPDATE holds SET held=1,reason='replay-loss-unreconciled' WHERE name='replay-loss'", tx, ("$g", generation), ("$s", sequence), ("$c", droppedCount), ("$b", droppedBytes));
            }
            else
                Execute("UPDATE replay_loss SET dropped_count=dropped_count+1,dropped_bytes=dropped_bytes+$b,reconciled=0 WHERE worker_generation=$g; UPDATE holds SET held=1,reason='replay-loss-unreconciled' WHERE name='replay-loss'", tx, ("$g", generation), ("$b", bytes));
            tx.Commit();
            throw new WorkerReplayLossException("Replay storage dropped an observational event; explicit replay-loss reconciliation is required.");
        }
        Execute("INSERT INTO events VALUES($g,$s,$k,$p,$b,$u); UPDATE event_generations SET last_sequence=$s WHERE worker_generation=$g; UPDATE meta SET value=CAST(value AS INTEGER)+1 WHERE key='next_event_sequence'", tx, ("$g", generation), ("$s", sequence), ("$k", kind), ("$p", payloadJson), ("$b", bytes), ("$u", Now()));
        tx.Commit(); return new WorkerEvent(generation, sequence, kind, payloadJson, bytes);
    }

    public WorkerReplayPage Replay(long generation, long afterSequence)
    {
        lock (_databaseGate) return ReplayLocked(generation, afterSequence);
    }

    private WorkerReplayPage ReplayLocked(long generation, long afterSequence)
    {
        var lastObject = Scalar("SELECT last_sequence FROM event_generations WHERE worker_generation=$g", null, ("$g", generation));
        if (lastObject is null)
        {
            var status = StatusLocked();
            throw ReplayGap("worker-generation-not-retained", generation, afterSequence, status.FirstRetainedSequence, status.LastSequence);
        }
        var last = Convert.ToInt64(lastObject, CultureInfo.InvariantCulture);
        var firstObject = Scalar("SELECT MIN(sequence) FROM events WHERE worker_generation=$g", null, ("$g", generation));
        var first = firstObject is null or DBNull ? last + 1 : Convert.ToInt64(firstObject, CultureInfo.InvariantCulture);
        if (afterSequence < 0 || afterSequence > last) throw ReplayGap("cursor-after-last-sequence", generation, afterSequence, first, last);
        if (afterSequence < first - 1)
        {
            var lossMarker = Scalar("SELECT marker_sequence FROM replay_loss WHERE worker_generation=$g AND reconciled=0", null, ("$g", generation));
            if (lossMarker is not null)
            {
                RecordReplayGap("loss", generation, afterSequence, first, last, generation, Convert.ToInt64(lossMarker, CultureInfo.InvariantCulture));
                throw new WorkerProtocolException("Replay cursor is invalid.");
            }
            throw ReplayGap("cursor-before-retained-boundary", generation, afterSequence, first, last);
        }
        using var command = _connection.CreateCommand(); command.CommandText = "SELECT sequence,kind,payload_json,byte_count FROM events WHERE worker_generation=$g AND sequence>$s ORDER BY sequence LIMIT $limit"; command.Parameters.AddWithValue("$g", generation); command.Parameters.AddWithValue("$s", afterSequence); command.Parameters.AddWithValue("$limit", checked(_options.ReplayPageEventLimit + 1));
        using var reader = command.ExecuteReader(); var result = new List<WorkerEvent>();
        var hasMore = false;
        while (reader.Read())
        {
            var item = new WorkerEvent(generation, reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3));
            if (result.Count >= _options.ReplayPageEventLimit || !FitsReplayPage(result.Append(item))) { hasMore = true; break; }
            result.Add(item);
        }
        var next = result.Count == 0 ? afterSequence : result[^1].Sequence;
        if (hasMore && next <= afterSequence) throw new WorkerProtocolException("Replay page cannot make progress.");
        return new WorkerReplayPage(result, hasMore, next);
    }

    private bool FitsReplayPage(WorkerEvent item) => FitsReplayPage([item]);
    private bool FitsReplayPage(IEnumerable<WorkerEvent> events)
    {
        var items = events.ToArray();
        var page = new WorkerReplayPage(items, true, items.LastOrDefault()?.Sequence ?? 0);
        return JsonSerializer.SerializeToUtf8Bytes(new { type = "result", operation = "replay", result = page }, WorkerProtocol.JsonOptions).Length < Math.Min(_options.ReplayPageByteLimit, WorkerProtocol.MaxControlFrameBytes);
    }

    public void Acknowledge(long generation, long sequence)
    {
        lock (_databaseGate) AcknowledgeLocked(generation, sequence);
    }

    private void AcknowledgeLocked(long generation, long sequence)
    {
        using var tx = _connection.BeginTransaction();
        var lastObject = Scalar("SELECT last_sequence FROM event_generations WHERE worker_generation=$g", tx, ("$g", generation));
        if (lastObject is null || sequence < 0 || sequence > Convert.ToInt64(lastObject, CultureInfo.InvariantCulture)) throw new WorkerProtocolException("Invalid event acknowledgment cursor.");
        var ackGeneration = Convert.ToInt64(Scalar("SELECT ack_worker_generation FROM replay WHERE singleton=1", tx), CultureInfo.InvariantCulture);
        var ackSequence = Convert.ToInt64(Scalar("SELECT ack_sequence FROM replay WHERE singleton=1", tx), CultureInfo.InvariantCulture);
        if (generation < ackGeneration || (generation == ackGeneration && sequence < ackSequence)) throw new WorkerProtocolException("Event acknowledgment cursor cannot regress.");
        Execute("UPDATE replay SET ack_worker_generation=$g,ack_sequence=$s WHERE singleton=1", tx, ("$g", generation), ("$s", sequence));
        tx.Commit();
    }

    public void ReconcileReplayLoss(long generation, long markerSequence)
    {
        lock (_databaseGate)
        {
            using var tx = _connection.BeginTransaction();
            Execute("UPDATE replay_loss SET reconciled=1 WHERE worker_generation=$g AND marker_sequence=$s AND reconciled=0", tx, ("$g", generation), ("$s", markerSequence));
            if (Convert.ToInt64(Scalar("SELECT changes()", tx), CultureInfo.InvariantCulture) != 1) throw new WorkerProtocolException("Replay loss marker is unknown or already reconciled.");
            Execute("DELETE FROM replay_gaps WHERE kind='loss' AND loss_marker_generation=$g AND loss_marker_sequence=$s AND reconciled=0; UPDATE holds SET held=0,reason=NULL WHERE name='replay-loss' AND NOT EXISTS(SELECT 1 FROM replay_loss WHERE reconciled=0); UPDATE holds SET held=0,reason=NULL WHERE name='replay-gap' AND NOT EXISTS(SELECT 1 FROM replay_gaps WHERE reconciled=0)", tx, ("$g", generation), ("$s", markerSequence));
            tx.Commit();
        }
    }

    public void ReconcileReplayGap(string id, long generation, long afterSequence, long firstRetainedSequence, long lastSequence)
    {
        WorkerProtocol.ValidateIdentifier(id, WorkerProtocol.MaxIdentifierLength, "replay gap id");
        lock (_databaseGate)
        {
            using var tx = _connection.BeginTransaction();
            Execute("DELETE FROM replay_gaps WHERE id=$id AND kind='status' AND worker_generation=$g AND after_sequence=$a AND first_retained=$f AND last_sequence=$l AND reconciled=0", tx,
                ("$id", id), ("$g", generation), ("$a", afterSequence), ("$f", firstRetainedSequence), ("$l", lastSequence));
            if (Convert.ToInt64(Scalar("SELECT changes()", tx), CultureInfo.InvariantCulture) != 1)
                throw new WorkerProtocolException("Replay gap state does not match the exact reconciliation marker.");
            Execute("UPDATE holds SET held=0,reason=NULL WHERE name='replay-gap' AND NOT EXISTS(SELECT 1 FROM replay_gaps WHERE reconciled=0)", tx);
            tx.Commit();
        }
    }

    public PendingPermission AddPermission(string requestId, string turnId, string decisionId, JsonElement parameters)
    {
        var epoch = Status().OwnershipEpoch;
        if (epoch < 1) throw new WorkerProtocolException("Permission requests require an active ownership lease.");
        return AddPermission(epoch, requestId, turnId, decisionId, parameters);
    }
    public PendingPermission AddPermission(long ownershipEpoch, string requestId, string turnId, string decisionId, JsonElement parameters)
    {
        lock (_databaseGate) return AddPermissionLocked(ownershipEpoch, requestId, turnId, decisionId, parameters);
    }

    private PendingPermission AddPermissionLocked(long ownershipEpoch, string requestId, string turnId, string decisionId, JsonElement parameters)
    {
        WorkerProtocol.ValidateIdentifier(requestId, WorkerProtocol.MaxIdentifierLength, "request id");
        WorkerProtocol.ValidateIdentifier(turnId, WorkerProtocol.MaxIdentifierLength, "turn id");
        WorkerProtocol.ValidateIdentifier(decisionId, WorkerProtocol.MaxIdentifierLength, "decision id");
        if (!parameters.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Array) throw new WorkerProtocolException("Permission frame has no offered options.");
        var optionIds = new List<string>();
        foreach (var option in options.EnumerateArray())
        {
            if (optionIds.Count >= 32 || option.ValueKind != JsonValueKind.Object || !option.TryGetProperty("optionId", out var id) || id.ValueKind != JsonValueKind.String) throw new WorkerProtocolException("Permission options are invalid or too numerous.");
            var value = id.GetString()!; WorkerProtocol.ValidateIdentifier(value, WorkerProtocol.MaxIdentifierLength, "permission option id");
            if (!optionIds.Contains(value, StringComparer.Ordinal)) optionIds.Add(value);
        }
        if (optionIds.Count == 0) throw new WorkerProtocolException("Permission frame has no offered options.");
        var payloadHash = Convert.ToHexString(WorkerProtocol.CanonicalPayloadHash(parameters)).ToLowerInvariant();
        var existing = GetPermissionLocked(decisionId);
        if (existing is not null)
        {
            if (existing.RequestId == requestId && existing.TurnId == turnId && existing.PayloadHash == payloadHash && existing.OptionIds.SequenceEqual(optionIds, StringComparer.Ordinal)) return existing;
            throw new WorkerProtocolException("Permission correlation conflicts with durable state.");
        }
        var optionJson = JsonSerializer.Serialize(optionIds, WorkerProtocol.JsonOptions);
        var byteCount = Encoding.UTF8.GetByteCount(optionJson) + payloadHash.Length + requestId.Length + turnId.Length + decisionId.Length;
        using var tx = _connection.BeginTransaction();
        var generation = Convert.ToInt64(Scalar("SELECT value FROM meta WHERE key='process_generation'", tx), CultureInfo.InvariantCulture);
        var currentEpoch = Convert.ToInt64(Scalar("SELECT epoch FROM lease WHERE singleton=1", tx), CultureInfo.InvariantCulture);
        var pendingCount = Convert.ToInt64(Scalar("SELECT COUNT(*) FROM pending_permissions WHERE state IN('pending','deciding','uncertain')", tx), CultureInfo.InvariantCulture);
        var pendingBytes = Convert.ToInt64(Scalar("SELECT COALESCE(SUM(byte_count),0) FROM pending_permissions WHERE state IN('pending','deciding','uncertain')", tx), CultureInfo.InvariantCulture);
        if (ownershipEpoch != currentEpoch) throw new WorkerProtocolException("Permission request ownership epoch is stale.");
        if (pendingCount >= _options.PendingPermissionLimit || pendingBytes + byteCount > _options.PendingPermissionByteLimit) throw new WorkerProtocolException("Pending permission capacity is exhausted.");
        Execute("INSERT INTO pending_permissions VALUES($d,$g,$e,$r,$t,$h,$o,$b,'pending',NULL,$u)", tx, ("$d", decisionId), ("$g", generation), ("$e", ownershipEpoch), ("$r", requestId), ("$t", turnId), ("$h", payloadHash), ("$o", optionJson), ("$b", byteCount), ("$u", Now())); tx.Commit();
        return new PendingPermission(generation, ownershipEpoch, requestId, turnId, decisionId, payloadHash, optionIds, "pending", null);
    }

    public PendingPermission BeginPermissionDecision(string decisionId, long generation, string requestId, string turnId, string decision) => BeginPermissionDecision(Status().OwnershipEpoch, decisionId, generation, requestId, turnId, decision);
    public PendingPermission BeginPermissionDecision(long ownershipEpoch, string decisionId, long generation, string requestId, string turnId, string decision)
    {
        lock (_databaseGate) return BeginPermissionDecisionLocked(ownershipEpoch, decisionId, generation, requestId, turnId, decision);
    }

    private PendingPermission BeginPermissionDecisionLocked(long ownershipEpoch, string decisionId, long generation, string requestId, string turnId, string decision)
    {
        var pending = GetPermissionLocked(decisionId) ?? throw new WorkerProtocolException("Permission decision is unknown.");
        var currentEpoch = Convert.ToInt64(Scalar("SELECT epoch FROM lease WHERE singleton=1"), CultureInfo.InvariantCulture);
        if (ownershipEpoch != currentEpoch) throw new WorkerProtocolException("Permission decision ownership epoch is stale.");
        if (pending.ProcessGeneration == generation && pending.OwnershipEpoch == ownershipEpoch && pending.RequestId == requestId && pending.TurnId == turnId && pending.State == "decided" && pending.Decision == decision) return pending;
        if (pending.ProcessGeneration != generation || pending.OwnershipEpoch != ownershipEpoch || pending.RequestId != requestId || pending.TurnId != turnId || pending.State != "pending" || !pending.OptionIds.Contains(decision, StringComparer.Ordinal))
            throw new WorkerProtocolException("Permission decision conflicts with durable pending state.");
        Execute("UPDATE pending_permissions SET state='deciding',decision=$v WHERE decision_id=$d AND state='pending'", null, ("$d", decisionId), ("$v", decision));
        return pending with { State = "deciding", Decision = decision };
    }
    public PendingPermission DecidePermission(string decisionId, long generation, string requestId, string turnId, string decision) => DecidePermission(Status().OwnershipEpoch, decisionId, generation, requestId, turnId, decision);
    public PendingPermission DecidePermission(long ownershipEpoch, string decisionId, long generation, string requestId, string turnId, string decision) { lock (_databaseGate) { var pending = BeginPermissionDecisionLocked(ownershipEpoch, decisionId, generation, requestId, turnId, decision); if (pending.State == "decided") return pending; CompletePermissionDecisionLocked(decisionId); return pending with { State = "decided" }; } }
    public void CompletePermissionDecision(string decisionId) { lock (_databaseGate) CompletePermissionDecisionLocked(decisionId); }
    private void CompletePermissionDecisionLocked(string decisionId) => Execute("UPDATE pending_permissions SET state='decided' WHERE decision_id=$d AND state='deciding'", null, ("$d", decisionId));
    public void InvalidatePermission(string decisionId)
    {
        lock (_databaseGate)
        {
            Execute("UPDATE pending_permissions SET state='invalidated' WHERE decision_id=$d AND state='pending'", null, ("$d", decisionId));
            if (Convert.ToInt64(Scalar("SELECT changes()"), CultureInfo.InvariantCulture) != 1) throw new WorkerProtocolException("Permission could not be invalidated from pending state.");
        }
    }
    public void MarkPermissionUncertain(string decisionId)
    {
        lock (_databaseGate)
        {
            using var tx = _connection.BeginTransaction();
            Execute("UPDATE pending_permissions SET state='uncertain' WHERE decision_id=$d AND state='deciding'; UPDATE holds SET held=1,reason='permission-decision-uncertain' WHERE name='permission'", tx, ("$d", decisionId));
            tx.Commit();
        }
    }

    public WorkerStatus Status()
    {
        lock (_databaseGate) return StatusLocked();
    }

    private WorkerStatus StatusLocked()
    {
        string processState;
        string? lifecycleHandle;
        long? observedPid;
        string? activeRequestId;
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = "SELECT state,lifecycle_handle,pid,active_request_id FROM process_slot WHERE singleton=1";
            using var reader = command.ExecuteReader(); reader.Read();
            processState = reader.GetString(0);
            lifecycleHandle = reader.IsDBNull(1) ? null : reader.GetString(1);
            observedPid = reader.IsDBNull(2) ? null : reader.GetInt64(2);
            activeRequestId = reader.IsDBNull(3) ? null : reader.GetString(3);
        }
        var epoch = Convert.ToInt64(Scalar("SELECT epoch FROM lease WHERE singleton=1"), CultureInfo.InvariantCulture);
        var active = Convert.ToInt64(Scalar("SELECT active FROM lease WHERE singleton=1"), CultureInfo.InvariantCulture) == 1 && _clock.MonotonicMilliseconds <= _leaseDeadline;
        var holdReasons = CurrentHoldReasonsLocked().ToList();
        if (Volatile.Read(ref _journalFailClosed) != 0 && !holdReasons.Contains("journal-failed", StringComparer.Ordinal)) holdReasons.Add("journal-failed");
        var held = holdReasons.Count > 0;
        var reason = held ? string.Join(",", holdReasons) : null;
        var last = MetaLongLocked("next_event_sequence") - 1;
        var workerGeneration = MetaLongLocked("worker_generation");
        var processGeneration = MetaLongLocked("process_generation");
        var firstObject = Scalar("SELECT MIN(sequence) FROM events WHERE worker_generation=$g", null, ("$g", workerGeneration)); var first = firstObject is null or DBNull ? last + 1 : Convert.ToInt64(firstObject, CultureInfo.InvariantCulture);
        var ackGeneration = Convert.ToInt64(Scalar("SELECT ack_worker_generation FROM replay WHERE singleton=1"), CultureInfo.InvariantCulture);
        var ackSequence = Convert.ToInt64(Scalar("SELECT ack_sequence FROM replay WHERE singleton=1"), CultureInfo.InvariantCulture);
        var replayGaps = CurrentReplayGapsLocked();
        var replayGapCount = Convert.ToInt32(Scalar("SELECT COUNT(*) FROM replay_gaps WHERE reconciled=0"), CultureInfo.InvariantCulture);
        return new WorkerStatus(workerGeneration, processGeneration, processState, lifecycleHandle, observedPid, activeRequestId, CurrentPermissionLocked(), epoch, active, held || Volatile.Read(ref _journalFailClosed) != 0, reason, holdReasons, first, last, ackGeneration, ackSequence, CurrentReplayLossLocked(), CurrentJournalFailureLocked(), replayGapCount, replayGaps);
    }

    public void SetHold(bool held, string? reason)
    {
        lock (_databaseGate)
        {
            if (reason is { Length: > 128 }) throw new WorkerProtocolException("Hold reason is too long.");
            SetHoldInternal("manual", held, held ? reason ?? "manual" : null);
        }
    }
    private void SetHoldInternal(string name, bool held, string? reason) => Execute("UPDATE holds SET held=$h,reason=$r WHERE name=$n", null, ("$h", held ? 1 : 0), ("$r", reason), ("$n", name));
    private WorkerProtocolException ReplayGap(string reason, long generation, long afterSequence, long firstRetainedSequence, long lastSequence)
    {
        _ = reason;
        RecordReplayGap("status", generation, afterSequence, firstRetainedSequence, lastSequence, null, null);
        return new WorkerProtocolException("Replay cursor is invalid.");
    }

    private ReplayGap RecordReplayGap(string kind, long generation, long afterSequence, long firstRetainedSequence, long lastSequence, long? lossMarkerGeneration, long? lossMarkerSequence)
    {
        var id = ReplayGapId(kind, generation, afterSequence, firstRetainedSequence, lastSequence, lossMarkerGeneration, lossMarkerSequence);
        using var tx = _connection.BeginTransaction();
        var existingId = Scalar("SELECT id FROM replay_gaps WHERE reconciled=0 AND kind=$k AND worker_generation=$g AND after_sequence=$a AND first_retained=$f AND last_sequence=$l AND loss_marker_generation IS $mg AND loss_marker_sequence IS $ms", tx,
            ("$k", kind), ("$g", generation), ("$a", afterSequence), ("$f", firstRetainedSequence), ("$l", lastSequence), ("$mg", lossMarkerGeneration), ("$ms", lossMarkerSequence))?.ToString();
        if (existingId is null)
        {
            var count = Convert.ToInt64(Scalar("SELECT COUNT(*) FROM replay_gaps WHERE reconciled=0", tx), CultureInfo.InvariantCulture);
            if (count >= ReplayGapLimit - 1)
            {
                kind = "overflow";
                generation = 0; afterSequence = 0; firstRetainedSequence = 0; lastSequence = 0;
                lossMarkerGeneration = null; lossMarkerSequence = null;
                id = ReplayGapId(kind, generation, afterSequence, firstRetainedSequence, lastSequence, null, null);
            }
            Execute("INSERT INTO replay_gaps VALUES($id,$k,$g,$a,$f,$l,$mg,$ms,0) ON CONFLICT DO NOTHING; UPDATE holds SET held=1,reason='replay-gap-unreconciled' WHERE name='replay-gap'", tx,
                ("$id", id), ("$k", kind), ("$g", generation), ("$a", afterSequence), ("$f", firstRetainedSequence), ("$l", lastSequence), ("$mg", lossMarkerGeneration), ("$ms", lossMarkerSequence));
        }
        else
        {
            id = existingId;
            Execute("UPDATE holds SET held=1,reason='replay-gap-unreconciled' WHERE name='replay-gap'", tx);
        }
        tx.Commit();
        return new ReplayGap(id, kind, generation, afterSequence, firstRetainedSequence, lastSequence, lossMarkerGeneration, lossMarkerSequence);
    }

    private static string ReplayGapId(string kind, long generation, long afterSequence, long firstRetainedSequence, long lastSequence, long? lossMarkerGeneration, long? lossMarkerSequence)
    {
        var tuple = string.Join(':', kind, generation.ToString(CultureInfo.InvariantCulture), afterSequence.ToString(CultureInfo.InvariantCulture), firstRetainedSequence.ToString(CultureInfo.InvariantCulture), lastSequence.ToString(CultureInfo.InvariantCulture), lossMarkerGeneration?.ToString(CultureInfo.InvariantCulture) ?? "-", lossMarkerSequence?.ToString(CultureInfo.InvariantCulture) ?? "-");
        return "gap:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tuple))).ToLowerInvariant();
    }
    private IReadOnlyList<string> CurrentHoldReasonsLocked()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT reason FROM holds WHERE held=1 ORDER BY CASE name WHEN 'manual' THEN 0 WHEN 'replay-gap' THEN 1 WHEN 'replay-loss' THEN 2 WHEN 'process' THEN 3 WHEN 'permission' THEN 4 WHEN 'ownership' THEN 5 WHEN 'transport' THEN 6 ELSE 7 END,name";
        using var reader = command.ExecuteReader();
        var reasons = new List<string>();
        while (reader.Read()) reasons.Add(reader.IsDBNull(0) ? "held" : reader.GetString(0));
        return reasons;
    }

    private PendingPermission? CurrentPermissionLocked() { using var command = _connection.CreateCommand(); command.CommandText = "SELECT process_generation,ownership_epoch,request_id,turn_id,decision_id,payload_hash,option_ids_json,state,decision FROM pending_permissions WHERE state IN('pending','deciding','uncertain') ORDER BY created_utc LIMIT 1"; using var reader = command.ExecuteReader(); return !reader.Read() ? null : ReadPermission(reader); }
    private PendingPermission? GetPermissionLocked(string id) { using var command = _connection.CreateCommand(); command.CommandText = "SELECT process_generation,ownership_epoch,request_id,turn_id,decision_id,payload_hash,option_ids_json,state,decision FROM pending_permissions WHERE decision_id=$d"; command.Parameters.AddWithValue("$d", id); using var reader = command.ExecuteReader(); return !reader.Read() ? null : ReadPermission(reader); }
    private static PendingPermission ReadPermission(SqliteDataReader reader) => new(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), JsonSerializer.Deserialize<string[]>(reader.GetString(6), WorkerProtocol.JsonOptions)!, reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8));
    private ReplayLoss? CurrentReplayLossLocked() { using var command = _connection.CreateCommand(); command.CommandText = "SELECT worker_generation,marker_sequence,dropped_count,dropped_bytes FROM replay_loss WHERE reconciled=0 ORDER BY worker_generation LIMIT 1"; using var reader = command.ExecuteReader(); return !reader.Read() ? null : new ReplayLoss(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3)); }
    private JournalFailure? CurrentJournalFailureLocked() { using var command = _connection.CreateCommand(); command.CommandText = "SELECT operation_id,worker_generation,error_category FROM journal_failures WHERE reconciled=0 ORDER BY created_utc,operation_id LIMIT 1"; using var reader = command.ExecuteReader(); return !reader.Read() ? null : new JournalFailure(reader.GetString(0), reader.GetInt64(1), reader.GetString(2)); }
    private IReadOnlyList<ReplayGap> CurrentReplayGapsLocked() { using var command = _connection.CreateCommand(); command.CommandText = "SELECT id,kind,worker_generation,after_sequence,first_retained,last_sequence,loss_marker_generation,loss_marker_sequence FROM replay_gaps WHERE reconciled=0 ORDER BY rowid LIMIT 128"; using var reader = command.ExecuteReader(); var gaps = new List<ReplayGap>(); while (reader.Read()) gaps.Add(new ReplayGap(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), reader.IsDBNull(6) ? null : reader.GetInt64(6), reader.IsDBNull(7) ? null : reader.GetInt64(7))); return gaps; }
    private StoredRequest? QueryRequest(string id, SqliteTransaction? tx) { lock (_databaseGate) return QueryRequestLocked(id, tx); }
    private StoredRequest? QueryRequestLocked(string id, SqliteTransaction? tx) { using var command = _connection.CreateCommand(); command.Transaction = tx; command.CommandText = "SELECT payload_hash,state,outcome_json,process_generation,ownership_epoch,turn_id,session_id FROM requests WHERE request_id=$id"; command.Parameters.AddWithValue("$id", id); using var reader = command.ExecuteReader(); return !reader.Read() ? null : new(id, reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetString(5), reader.GetString(6)); }
    private StoredCancellation? QueryCancellation(string id, SqliteTransaction? tx) { lock (_databaseGate) return QueryCancellationLocked(id, tx); }
    private StoredCancellation? QueryCancellationLocked(string id, SqliteTransaction? tx) { using var command = _connection.CreateCommand(); command.Transaction = tx; command.CommandText = "SELECT target_request_id,payload_hash,state,process_generation,ownership_epoch FROM cancellations WHERE cancellation_id=$id"; command.Parameters.AddWithValue("$id", id); using var reader = command.ExecuteReader(); return !reader.Read() ? null : new(id, reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetInt64(4)); }
    private static string RequirePromptSession(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String || method.GetString() != "session/prompt") throw new WorkerProtocolException("Only session/prompt submissions are supported.");
        if (!payload.TryGetProperty("params", out var parameters) || parameters.ValueKind != JsonValueKind.Object || !parameters.TryGetProperty("sessionId", out var session) || session.ValueKind != JsonValueKind.String || session.GetString() is not { } sessionId) throw new WorkerProtocolException("session/prompt requires sessionId.");
        WorkerProtocol.ValidateIdentifier(sessionId, WorkerProtocol.MaxIdentifierLength, "session id");
        return sessionId;
    }
    private void IncrementWorkerGenerationAndReconcileStartup()
    {
        using var tx = _connection.BeginTransaction();
        Execute("UPDATE meta SET value=CAST(value AS INTEGER)+1 WHERE key='worker_generation'; INSERT INTO event_generations(worker_generation,last_sequence) VALUES((SELECT CAST(value AS INTEGER) FROM meta WHERE key='worker_generation'),0); UPDATE meta SET value='1' WHERE key='next_event_sequence'; UPDATE lease SET active=0 WHERE singleton=1; UPDATE pending_permissions SET state='invalidated' WHERE state IN('pending','deciding','uncertain'); UPDATE requests SET state='uncertain' WHERE state IN('forwarding','forwarded'); UPDATE cancellations SET state='uncertain' WHERE state='forwarding'; UPDATE process_slot SET state='exited',lifecycle_handle=NULL,active_request_id=NULL,pid=NULL,updated_utc=$u WHERE singleton=1 AND state IN('starting','running','protocol-failed','transport-uncertain'); UPDATE holds SET held=0,reason=NULL WHERE name IN('ownership','permission'); UPDATE holds SET held=1,reason='process-exited' WHERE name='process' AND EXISTS(SELECT 1 FROM process_slot WHERE singleton=1 AND state='exited'); DELETE FROM pending_permissions WHERE state IN('decided','invalidated') OR process_generation<(SELECT CAST(value AS INTEGER) FROM meta WHERE key='process_generation'); DELETE FROM cancellations WHERE process_generation<(SELECT CAST(value AS INTEGER) FROM meta WHERE key='process_generation') AND state='forwarded'", tx, ("$u", Now()));
        tx.Commit();
    }
    internal void CheckpointForTests()
    {
        lock (_databaseGate) Execute("PRAGMA wal_checkpoint(TRUNCATE)");
    }

    private string Meta(string key) { lock (_databaseGate) return MetaLocked(key); }
    private string MetaLocked(string key) => Scalar("SELECT value FROM meta WHERE key=$k", null, ("$k", key))?.ToString() ?? throw new WorkerStoreException("Worker journal metadata is incomplete.");
    private long MetaLong(string key) { lock (_databaseGate) return MetaLongLocked(key); }
    private long MetaLongLocked(string key) => long.Parse(MetaLocked(key), CultureInfo.InvariantCulture);
    private void InsertMeta(string key, string value, SqliteTransaction tx) => Execute("INSERT INTO meta VALUES($k,$v)", tx, ("$k", key), ("$v", value));
    private string Now() => _clock.UtcNow.ToString("O", CultureInfo.InvariantCulture);
    private object? Scalar(string sql, SqliteTransaction? tx = null, params (string, object?)[] args) { lock (_databaseGate) { using var command = _connection.CreateCommand(); command.Transaction = tx; command.CommandText = sql; foreach (var argument in args) command.Parameters.AddWithValue(argument.Item1, argument.Item2 ?? DBNull.Value); return command.ExecuteScalar(); } }
    private void Execute(string sql, SqliteTransaction? tx = null, params (string, object?)[] args) { lock (_databaseGate) { using var command = _connection.CreateCommand(); command.Transaction = tx; command.CommandText = sql; foreach (var argument in args) command.Parameters.AddWithValue(argument.Item1, argument.Item2 ?? DBNull.Value); command.ExecuteNonQuery(); } }

    private void SecureDatabaseFiles() { if (OperatingSystem.IsWindows()) return; foreach (var path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" }) if (File.Exists(path)) { ValidateAbsentOrPrivateRegular(path, false, _options.ExpectedBridgeUid, allowMultipleLinks: false, requireMode: false); File.SetUnixFileMode(path, PrivateFileMode); ValidateAbsentOrPrivateRegular(path, false, _options.ExpectedBridgeUid); } }
    private static void CleanOwnedDatabaseTemps(string directory, int expectedUid)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(directory, ".bridge.db.*.tmp*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(path);
            var baseName = name.EndsWith("-wal", StringComparison.Ordinal) || name.EndsWith("-shm", StringComparison.Ordinal) ? name[..^4] : name;
            var token = baseName[".bridge.db.".Length..^".tmp".Length];
            if (token.Length != 32 || !token.All(Uri.IsHexDigit)) throw new WorkerStoreException("An unexpected worker database temporary object requires reconciliation.");
            ValidateAbsentOrPrivateRegular(path, false, expectedUid, requireMode: false);
            if (!OperatingSystem.IsWindows() && File.GetUnixFileMode(path) != PrivateFileMode) throw new WorkerStoreException("A worker database temporary has unsafe mode.");
            File.Delete(path);
        }
    }
    internal static void ValidateOwnedSocket(string path, int expectedUid)
    {
        if (!OperatingSystem.IsLinux()) throw new WorkerStoreException("Secure stale socket validation requires Linux.");
        var stat = StatPath(path, noFollow: true);
        if ((stat.stx_mode & 0xF000) != 0xC000 || stat.stx_nlink != 1 || (expectedUid >= 0 && stat.stx_uid != (uint)expectedUid)) throw new WorkerStoreException("The existing bridge socket is not a stale owned socket.");
    }
    internal static void ValidateControlDirectory(string path, int expectedUid)
    {
        if (OperatingSystem.IsWindows()) { if (!Directory.Exists(path)) throw new WorkerStoreException("The bridge-private control directory is invalid."); return; }
        var stat = StatPath(path, noFollow: true);
        if ((stat.stx_mode & 0xF000) != 0x4000 || stat.stx_nlink < 1 || (expectedUid >= 0 && stat.stx_uid != (uint)expectedUid) || File.GetUnixFileMode(path) != PrivateDirectoryMode)
            throw new WorkerStoreException("The bridge-private control directory owner, type, or mode is invalid.");
    }
    internal static void ValidateAbsentOrPrivateRegular(string path, bool allowAbsent, int expectedUid, bool allowMultipleLinks = false, bool requireMode = true)
    {
        if (OperatingSystem.IsWindows()) { if (!allowAbsent && !File.Exists(path)) throw new WorkerStoreException("A required private file is missing."); return; }
        Statx stat;
        try { stat = StatPath(path, noFollow: true); }
        catch (WorkerStoreException) when (allowAbsent && Marshal.GetLastPInvokeError() == 2) { return; }
        if ((stat.stx_mode & 0xF000) != 0x8000 || (!allowMultipleLinks && stat.stx_nlink != 1) || (expectedUid >= 0 && stat.stx_uid != (uint)expectedUid) || (requireMode && File.GetUnixFileMode(path) != PrivateFileMode))
            throw new WorkerStoreException("A bridge-private file owner, link count, type, or mode is invalid.");
    }
    private static FileStream AcquireInstanceLock(string path, int expectedUid)
    {
        if (OperatingSystem.IsWindows())
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { throw new WorkerStoreException("Another worker journal instance is active or its lock file is invalid.", exception); }
        }

        const int openReadWrite = 0x2;
        const int openCreate = 0x40;
        const int openCloseOnExec = 0x80000;
        const int openNoFollow = 0x20000;
        const int lockExclusive = 0x2;
        const int lockNonBlocking = 0x4;
        var fd = open(path, openReadWrite | openCreate | openCloseOnExec | openNoFollow, (uint)PrivateFileMode);
        if (fd < 0) throw new WorkerStoreException("The worker instance lock file could not be opened safely.");
        var handle = new Microsoft.Win32.SafeHandles.SafeFileHandle((IntPtr)fd, ownsHandle: true);
        try
        {
            var stream = new FileStream(handle, FileAccess.ReadWrite, 1, isAsync: false);
            SecureDescriptor(stream.SafeFileHandle, PrivateFileMode, expectedUid, requireSingleLink: true, setMode: false);
            var descriptor = StatPath("/proc/self/fd/" + fd.ToString(CultureInfo.InvariantCulture), noFollow: false);
            var linked = StatPath(path, noFollow: true);
            if (descriptor.stx_ino != linked.stx_ino || descriptor.devMajor != linked.devMajor || descriptor.devMinor != linked.devMinor)
            {
                stream.Dispose();
                throw new WorkerStoreException("The worker instance lock path changed during validation.");
            }
            if (flock(stream.SafeFileHandle, lockExclusive | lockNonBlocking) != 0)
            {
                stream.Dispose();
                throw new WorkerStoreException("Another worker journal instance is active.");
            }
            return stream;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static void SecureDescriptor(Microsoft.Win32.SafeHandles.SafeFileHandle handle, UnixFileMode mode, int expectedUid, bool requireSingleLink, bool setMode = true)
    {
        if (OperatingSystem.IsWindows()) return;
        if (setMode && fchmod(handle, (uint)mode) != 0) throw new WorkerStoreException("A private file mode could not be secured.");
        var path = "/proc/self/fd/" + handle.DangerousGetHandle().ToInt64().ToString(CultureInfo.InvariantCulture); var stat = StatPath(path, noFollow: false);
        if ((stat.stx_mode & 0xF000) != 0x8000 || (stat.stx_mode & 0x1FF) != (ushort)mode || (requireSingleLink && stat.stx_nlink != 1) || (expectedUid >= 0 && stat.stx_uid != (uint)expectedUid)) throw new WorkerStoreException("A private file descriptor failed validation.");
    }
    private static Statx StatPath(string path, bool noFollow) { if (RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64)) throw new WorkerStoreException("Secure Linux filesystem validation is unsupported on this architecture."); if (statx(-100, path, noFollow ? 0x100 : 0, 0x7ff, out var stat) != 0) throw new WorkerStoreException("A private filesystem object could not be validated."); return stat; }
    private static void FsyncDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        var fd = open(path, 0x10000 | 0x80000 | 0x20000);
        if (fd < 0) throw new WorkerStoreException("The worker control directory could not be synchronized.");
        try { if (fsync(fd) != 0) throw new WorkerStoreException("The worker control directory could not be synchronized."); }
        finally { _ = close(fd); }
    }

    public void Dispose()
    {
        lock (_databaseGate)
        {
            if (_disposed) return; _disposed = true;
            _connection.Close(); _connection.Dispose();
            if (!OperatingSystem.IsWindows()) _ = flock(_instanceLock.SafeFileHandle, 0x8);
            _instanceLock.Dispose();
        }
    }

    private sealed class ZeroingBuffer(byte[] value) : IDisposable { public void Dispose() => CryptographicOperations.ZeroMemory(value); }
    [StructLayout(LayoutKind.Sequential)] private struct StatxTimestamp { public long tv_sec; public uint tv_nsec; private int reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct Statx { public uint stx_mask, stx_blksize; public ulong stx_attributes; public uint stx_nlink, stx_uid, stx_gid; public ushort stx_mode; private ushort spare0; public ulong stx_ino, stx_size, stx_blocks, stx_attributes_mask; private StatxTimestamp atime, btime, ctime, mtime; private uint rdevMajor, rdevMinor; public uint devMajor, devMinor; private ulong mountId; private uint dioMemAlign, dioOffsetAlign; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 12)] private ulong[]? spare3; }
    [DllImport("libc", SetLastError = true)] private static extern int statx(int dirfd, string pathname, int flags, uint mask, out Statx statxbuf);
    [DllImport("libc", SetLastError = true)] private static extern int fchmod(Microsoft.Win32.SafeHandles.SafeFileHandle fd, uint mode);
    [DllImport("libc", SetLastError = true)] private static extern int open(string path, int flags);
    [DllImport("libc", SetLastError = true, EntryPoint = "open")] private static extern int open(string path, int flags, uint mode);
    [DllImport("libc", SetLastError = true)] private static extern int flock(Microsoft.Win32.SafeHandles.SafeFileHandle fd, int operation);
    [DllImport("libc", SetLastError = true)] private static extern int fsync(int fd);
    [DllImport("libc", SetLastError = true)] private static extern int close(int fd);
}
