using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HVO.AgentControl.Worker;

public sealed class WorkerStore : IDisposable
{
    public const int SchemaVersion = 4;
    public const string SchemaSignature = "hvo-worker-bridge-v4-20260915";
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
            CREATE TABLE requests(request_id TEXT PRIMARY KEY, payload_hash TEXT NOT NULL, state TEXT NOT NULL CHECK(state IN('persisted','forwarding','forwarded','completed','failed','uncertain')), outcome_json TEXT, process_generation INTEGER NOT NULL CHECK(process_generation>=0), turn_id TEXT, created_utc TEXT NOT NULL);
            CREATE TABLE cancellations(cancellation_id TEXT PRIMARY KEY, target_request_id TEXT NOT NULL, payload_hash TEXT NOT NULL, state TEXT NOT NULL CHECK(state IN('persisted','forwarding','forwarded','uncertain')), process_generation INTEGER NOT NULL CHECK(process_generation>=0), created_utc TEXT NOT NULL);
            CREATE TABLE event_generations(worker_generation INTEGER PRIMARY KEY CHECK(worker_generation>0), last_sequence INTEGER NOT NULL CHECK(last_sequence>=0));
            CREATE TABLE events(worker_generation INTEGER NOT NULL CHECK(worker_generation>0), sequence INTEGER NOT NULL CHECK(sequence>0), kind TEXT NOT NULL, payload_json TEXT NOT NULL, byte_count INTEGER NOT NULL CHECK(byte_count>=0), created_utc TEXT NOT NULL, PRIMARY KEY(worker_generation, sequence), FOREIGN KEY(worker_generation) REFERENCES event_generations(worker_generation));
            CREATE TABLE replay(singleton INTEGER PRIMARY KEY CHECK(singleton=1), ack_worker_generation INTEGER NOT NULL CHECK(ack_worker_generation>=0), ack_sequence INTEGER NOT NULL CHECK(ack_sequence>=0));
            CREATE TABLE pending_permissions(decision_id TEXT PRIMARY KEY, process_generation INTEGER NOT NULL CHECK(process_generation>=0), request_id TEXT NOT NULL, turn_id TEXT NOT NULL, payload_hash TEXT NOT NULL, option_ids_json TEXT NOT NULL, state TEXT NOT NULL CHECK(state IN('pending','deciding','decided','uncertain','invalidated')), decision TEXT, created_utc TEXT NOT NULL);
            CREATE TABLE process_slot(singleton INTEGER PRIMARY KEY CHECK(singleton=1), state TEXT NOT NULL CHECK(state IN('stopped','starting','running','exited','uncertain')), lifecycle_handle TEXT, session_id TEXT, active_request_id TEXT, pid INTEGER, updated_utc TEXT NOT NULL);
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
        Execute("INSERT INTO lease VALUES(1,0,NULL,NULL,NULL,0); INSERT INTO replay VALUES(1,0,0); INSERT INTO process_slot VALUES(1,'stopped',NULL,NULL,NULL,NULL,$now); INSERT INTO holds VALUES('dispatch',0,NULL);", tx, ("$now", Now()));
        tx.Commit();
    }

    private void ValidateSchema()
    {
        try
        {
            var expected = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["index:ix_pending_permissions_state"] = "CREATE INDEX ix_pending_permissions_state ON pending_permissions(state, created_utc)",
                ["table:cancellations"] = "CREATE TABLE cancellations(cancellation_id TEXT PRIMARY KEY, target_request_id TEXT NOT NULL, payload_hash TEXT NOT NULL, state TEXT NOT NULL CHECK(state IN('persisted','forwarding','forwarded','uncertain')), process_generation INTEGER NOT NULL CHECK(process_generation>=0), created_utc TEXT NOT NULL)",
                ["table:event_generations"] = "CREATE TABLE event_generations(worker_generation INTEGER PRIMARY KEY CHECK(worker_generation>0), last_sequence INTEGER NOT NULL CHECK(last_sequence>=0))",
                ["table:events"] = "CREATE TABLE events(worker_generation INTEGER NOT NULL CHECK(worker_generation>0), sequence INTEGER NOT NULL CHECK(sequence>0), kind TEXT NOT NULL, payload_json TEXT NOT NULL, byte_count INTEGER NOT NULL CHECK(byte_count>=0), created_utc TEXT NOT NULL, PRIMARY KEY(worker_generation, sequence), FOREIGN KEY(worker_generation) REFERENCES event_generations(worker_generation))",
                ["table:holds"] = "CREATE TABLE holds(name TEXT PRIMARY KEY, held INTEGER NOT NULL CHECK(held IN(0,1)), reason TEXT)",
                ["table:lease"] = "CREATE TABLE lease(singleton INTEGER PRIMARY KEY CHECK(singleton=1), epoch INTEGER NOT NULL CHECK(epoch>=0), controller_id TEXT, connection_nonce TEXT, observed_utc TEXT, active INTEGER NOT NULL CHECK(active IN(0,1)))",
                ["table:meta"] = "CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT NOT NULL)",
                ["table:pending_permissions"] = "CREATE TABLE pending_permissions(decision_id TEXT PRIMARY KEY, process_generation INTEGER NOT NULL CHECK(process_generation>=0), request_id TEXT NOT NULL, turn_id TEXT NOT NULL, payload_hash TEXT NOT NULL, option_ids_json TEXT NOT NULL, state TEXT NOT NULL CHECK(state IN('pending','deciding','decided','uncertain','invalidated')), decision TEXT, created_utc TEXT NOT NULL)",
                ["table:process_slot"] = "CREATE TABLE process_slot(singleton INTEGER PRIMARY KEY CHECK(singleton=1), state TEXT NOT NULL CHECK(state IN('stopped','starting','running','exited','uncertain')), lifecycle_handle TEXT, session_id TEXT, active_request_id TEXT, pid INTEGER, updated_utc TEXT NOT NULL)",
                ["table:replay"] = "CREATE TABLE replay(singleton INTEGER PRIMARY KEY CHECK(singleton=1), ack_worker_generation INTEGER NOT NULL CHECK(ack_worker_generation>=0), ack_sequence INTEGER NOT NULL CHECK(ack_sequence>=0))",
                ["table:requests"] = "CREATE TABLE requests(request_id TEXT PRIMARY KEY, payload_hash TEXT NOT NULL, state TEXT NOT NULL CHECK(state IN('persisted','forwarding','forwarded','completed','failed','uncertain')), outcome_json TEXT, process_generation INTEGER NOT NULL CHECK(process_generation>=0), turn_id TEXT, created_utc TEXT NOT NULL)",
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
        Execute("UPDATE lease SET epoch=epoch+1,controller_id=$c,connection_nonce=$n,observed_utc=$u,active=1 WHERE singleton=1; UPDATE holds SET held=0,reason=NULL WHERE name='dispatch' AND reason='lease-expired'", tx, ("$c", controllerId), ("$n", connectionNonce), ("$u", Now()));
        var epoch = Convert.ToInt64(Scalar("SELECT epoch FROM lease WHERE singleton=1", tx), CultureInfo.InvariantCulture);
        tx.Commit();
        _leaseDeadline = checked(_clock.MonotonicMilliseconds + (long)_options.LeaseLifetime.TotalMilliseconds);
        return new Lease(epoch, controllerId, connectionNonce, _clock.UtcNow);
    }

    public void RequireLease(long epoch, string nonce)
    {
        lock (_databaseGate) RequireLeaseLocked(epoch, nonce);
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
            if (identityMatches && expired) SetHoldInternal(true, "lease-expired");
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
        Execute("UPDATE meta SET value=CAST(value AS INTEGER)+1 WHERE key='process_generation'; UPDATE pending_permissions SET state='invalidated' WHERE state IN('pending','deciding'); UPDATE process_slot SET state='starting',lifecycle_handle=NULL,session_id=NULL,active_request_id=NULL,pid=NULL,updated_utc=$u WHERE singleton=1", tx, ("$u", Now()));
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
        Execute("UPDATE holds SET held=0,reason=NULL WHERE name='dispatch' AND reason='process-exited'", tx);
        tx.Commit();
    }
    public void FailProcessStart(bool uncertain) { lock (_databaseGate) Execute("UPDATE process_slot SET state=$s,updated_utc=$u WHERE singleton=1 AND state='starting'", null, ("$s", uncertain ? "uncertain" : "stopped"), ("$u", Now())); }
    public void SetProcess(string state, string? sessionId = null, long? pid = null) { lock (_databaseGate) Execute("UPDATE process_slot SET state=$s,session_id=COALESCE($i,session_id),pid=$p,updated_utc=$u WHERE singleton=1", null, ("$s", state), ("$i", sessionId), ("$p", pid), ("$u", Now())); }
    public void SetActiveRequest(string? requestId) { lock (_databaseGate) Execute("UPDATE process_slot SET active_request_id=$r,updated_utc=$u WHERE singleton=1", null, ("$r", requestId), ("$u", Now())); }

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
        if (turnId is not null) WorkerProtocol.ValidateIdentifier(turnId, WorkerProtocol.MaxIdentifierLength, "turn id");
        var hash = Convert.ToHexString(WorkerProtocol.CanonicalPayloadHash(payload)).ToLowerInvariant();
        using var tx = _connection.BeginTransaction();
        var existing = QueryRequest(id, tx);
        if (existing is not null)
        {
            if (existing.PayloadHash != hash) throw new WorkerProtocolException("Request id was reused with a different payload.");
            tx.Commit(); return existing;
        }
        EnsureDispatchAllowed(tx, epoch, nonce);
        var generation = Convert.ToInt64(Scalar("SELECT value FROM meta WHERE key='process_generation'", tx), CultureInfo.InvariantCulture);
        Execute("INSERT INTO requests VALUES($id,$h,'forwarding',NULL,$g,$t,$u)", tx, ("$id", id), ("$h", hash), ("$g", generation), ("$t", turnId), ("$u", Now()));
        tx.Commit(); return new StoredRequest(id, hash, "forwarding", null, generation, turnId);
    }

    public StoredRequest RegisterRequest(string id, JsonElement payload, string? turnId)
    {
        lock (_databaseGate) return RegisterRequestLocked(id, payload, turnId);
    }

    private StoredRequest RegisterRequestLocked(string id, JsonElement payload, string? turnId)
    {
        WorkerProtocol.ValidateIdentifier(id, WorkerProtocol.MaxIdentifierLength, "request id");
        var hash = Convert.ToHexString(WorkerProtocol.CanonicalPayloadHash(payload)).ToLowerInvariant();
        using var tx = _connection.BeginTransaction(); var existing = QueryRequest(id, tx);
        if (existing is not null) { if (existing.PayloadHash != hash) throw new WorkerProtocolException("Request id was reused with a different payload."); tx.Commit(); return existing; }
        var generation = Convert.ToInt64(Scalar("SELECT value FROM meta WHERE key='process_generation'", tx), CultureInfo.InvariantCulture);
        Execute("INSERT INTO requests VALUES($id,$h,'persisted',NULL,$g,$t,$u)", tx, ("$id", id), ("$h", hash), ("$g", generation), ("$t", turnId), ("$u", Now())); tx.Commit();
        return new StoredRequest(id, hash, "persisted", null, generation, turnId);
    }

    private void EnsureDispatchAllowed(SqliteTransaction tx, long epoch, string nonce)
    {
        using var command = _connection.CreateCommand(); command.Transaction = tx;
        command.CommandText = "SELECT l.epoch,l.connection_nonce,l.active,p.state,h.held,h.reason FROM lease l,process_slot p,holds h WHERE l.singleton=1 AND p.singleton=1 AND h.name='dispatch'";
        using var reader = command.ExecuteReader(); reader.Read();
        if (reader.GetInt64(0) != epoch || reader.IsDBNull(1) || reader.GetString(1) != nonce || reader.GetInt64(2) != 1 || _clock.MonotonicMilliseconds > _leaseDeadline)
            throw new WorkerProtocolException("The ownership lease is stale or expired.");
        if (reader.GetInt64(4) != 0) throw new WorkerProtocolException("Dispatch is held.");
        if (reader.GetString(3) != "running") throw new WorkerProtocolException("The worker process is not running.");
    }

    public void MarkForwarding(string id) { lock (_databaseGate) Execute("UPDATE requests SET state='forwarding' WHERE request_id=$id AND state='persisted'", null, ("$id", id)); }
    public void MarkForwarded(string id) { lock (_databaseGate) Execute("UPDATE requests SET state='forwarded' WHERE request_id=$id AND state='forwarding'", null, ("$id", id)); }
    public void MarkUncertain(string id) { lock (_databaseGate) Execute("UPDATE requests SET state='uncertain' WHERE request_id=$id AND state IN('persisted','forwarding','forwarded')", null, ("$id", id)); }
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
            if (target.State is not ("forwarding" or "forwarded")) throw new WorkerProtocolException("Cancellation target is not active.");
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
            var currentGeneration = Convert.ToInt64(Scalar("SELECT value FROM meta WHERE key='process_generation'", tx), CultureInfo.InvariantCulture);
            var processState = Scalar("SELECT state FROM process_slot WHERE singleton=1", tx)?.ToString();
            if (target.ProcessGeneration != currentGeneration || processState != "running")
                throw new WorkerProtocolException("Cancellation target process is no longer running.");
            Execute("INSERT INTO cancellations VALUES($id,$target,$hash,'persisted',$generation,$utc)", tx,
                ("$id", cancellationId), ("$target", targetRequestId), ("$hash", hash),
                ("$generation", target.ProcessGeneration), ("$utc", Now()));
            tx.Commit();
            return new StoredCancellation(cancellationId, targetRequestId, hash, "persisted", target.ProcessGeneration);
        }
    }

    public void MarkCancellationForwarding(string id) { lock (_databaseGate) Execute("UPDATE cancellations SET state='forwarding' WHERE cancellation_id=$id AND state='persisted'", null, ("$id", id)); }
    public void MarkCancellationForwarded(string id) { lock (_databaseGate) Execute("UPDATE cancellations SET state='forwarded' WHERE cancellation_id=$id AND state='forwarding'", null, ("$id", id)); }
    public void MarkCancellationUncertain(string id) { lock (_databaseGate) Execute("UPDATE cancellations SET state='uncertain' WHERE cancellation_id=$id AND state IN('persisted','forwarding')", null, ("$id", id)); }
    public StoredCancellation? GetCancellation(string id) { lock (_databaseGate) return QueryCancellationLocked(id, null); }

    public WorkerEvent AppendEvent(string kind, string payloadJson)
    {
        lock (_databaseGate) return AppendEventLocked(kind, payloadJson);
    }

    private WorkerEvent AppendEventLocked(string kind, string payloadJson)
    {
        WorkerProtocol.ValidateIdentifier(kind, 64, "event kind");
        var bytes = Encoding.UTF8.GetByteCount(payloadJson);
        if (bytes > _options.EventByteLimit) { SetHoldInternal(true, "replay-overflow"); throw new WorkerProtocolException("Event exceeds the replay byte limit and was not retained."); }
        using var tx = _connection.BeginTransaction();
        var generation = Convert.ToInt64(Scalar("SELECT value FROM meta WHERE key='worker_generation'", tx), CultureInfo.InvariantCulture);
        var ackGeneration = Convert.ToInt64(Scalar("SELECT ack_worker_generation FROM replay WHERE singleton=1", tx), CultureInfo.InvariantCulture);
        var ackSequence = Convert.ToInt64(Scalar("SELECT ack_sequence FROM replay WHERE singleton=1", tx), CultureInfo.InvariantCulture);
        Execute("DELETE FROM events WHERE worker_generation<$g OR (worker_generation=$g AND sequence<=$s)", tx, ("$g", ackGeneration), ("$s", ackSequence));
        var count = Convert.ToInt64(Scalar("SELECT COUNT(*) FROM events", tx), CultureInfo.InvariantCulture);
        var total = Convert.ToInt64(Scalar("SELECT COALESCE(SUM(byte_count),0) FROM events", tx), CultureInfo.InvariantCulture);
        if (count + 1 > _options.EventLimit || total + bytes > _options.EventByteLimit)
        {
            Execute("UPDATE holds SET held=1,reason='replay-overflow' WHERE name='dispatch'", tx); tx.Commit();
            throw new WorkerProtocolException("Replay storage is full; the new observational event was not retained.");
        }
        var sequence = Convert.ToInt64(Scalar("SELECT value FROM meta WHERE key='next_event_sequence'", tx), CultureInfo.InvariantCulture);
        Execute("UPDATE holds SET held=0,reason=NULL WHERE name='dispatch' AND reason='replay-overflow'; INSERT INTO events VALUES($g,$s,$k,$p,$b,$u); UPDATE event_generations SET last_sequence=$s WHERE worker_generation=$g; UPDATE meta SET value=CAST(value AS INTEGER)+1 WHERE key='next_event_sequence'", tx, ("$g", generation), ("$s", sequence), ("$k", kind), ("$p", payloadJson), ("$b", bytes), ("$u", Now()));
        tx.Commit(); return new WorkerEvent(generation, sequence, kind, payloadJson, bytes);
    }

    public IReadOnlyList<WorkerEvent> Replay(long generation, long afterSequence)
    {
        lock (_databaseGate) return ReplayLocked(generation, afterSequence);
    }

    private IReadOnlyList<WorkerEvent> ReplayLocked(long generation, long afterSequence)
    {
        var lastObject = Scalar("SELECT last_sequence FROM event_generations WHERE worker_generation=$g", null, ("$g", generation));
        if (lastObject is null) throw ReplayGap("worker-generation-not-retained");
        var last = Convert.ToInt64(lastObject, CultureInfo.InvariantCulture);
        if (afterSequence < 0 || afterSequence > last) throw ReplayGap("cursor-after-last-sequence");
        var firstObject = Scalar("SELECT MIN(sequence) FROM events WHERE worker_generation=$g", null, ("$g", generation));
        var first = firstObject is null or DBNull ? last + 1 : Convert.ToInt64(firstObject, CultureInfo.InvariantCulture);
        if (afterSequence < first - 1) throw ReplayGap("cursor-before-retained-boundary");
        using var command = _connection.CreateCommand(); command.CommandText = "SELECT sequence,kind,payload_json,byte_count FROM events WHERE worker_generation=$g AND sequence>$s ORDER BY sequence"; command.Parameters.AddWithValue("$g", generation); command.Parameters.AddWithValue("$s", afterSequence);
        using var reader = command.ExecuteReader(); var result = new List<WorkerEvent>();
        while (reader.Read()) result.Add(new WorkerEvent(generation, reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3)));
        return result;
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

    public PendingPermission AddPermission(string requestId, string turnId, string decisionId, JsonElement parameters)
    {
        lock (_databaseGate) return AddPermissionLocked(requestId, turnId, decisionId, parameters);
    }

    private PendingPermission AddPermissionLocked(string requestId, string turnId, string decisionId, JsonElement parameters)
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
        using var tx = _connection.BeginTransaction(); var generation = Convert.ToInt64(Scalar("SELECT value FROM meta WHERE key='process_generation'", tx), CultureInfo.InvariantCulture);
        Execute("INSERT INTO pending_permissions VALUES($d,$g,$r,$t,$h,$o,'pending',NULL,$u)", tx, ("$d", decisionId), ("$g", generation), ("$r", requestId), ("$t", turnId), ("$h", payloadHash), ("$o", optionJson), ("$u", Now())); tx.Commit();
        return new PendingPermission(generation, requestId, turnId, decisionId, payloadHash, optionIds, "pending", null);
    }

    public PendingPermission BeginPermissionDecision(string decisionId, long generation, string requestId, string turnId, string decision)
    {
        lock (_databaseGate) return BeginPermissionDecisionLocked(decisionId, generation, requestId, turnId, decision);
    }

    private PendingPermission BeginPermissionDecisionLocked(string decisionId, long generation, string requestId, string turnId, string decision)
    {
        var pending = GetPermissionLocked(decisionId) ?? throw new WorkerProtocolException("Permission decision is unknown.");
        if (pending.ProcessGeneration == generation && pending.RequestId == requestId && pending.TurnId == turnId && pending.State == "decided" && pending.Decision == decision) return pending;
        if (pending.ProcessGeneration != generation || pending.RequestId != requestId || pending.TurnId != turnId || pending.State != "pending" || !pending.OptionIds.Contains(decision, StringComparer.Ordinal))
            throw new WorkerProtocolException("Permission decision conflicts with durable pending state.");
        Execute("UPDATE pending_permissions SET state='deciding',decision=$v WHERE decision_id=$d AND state='pending'", null, ("$d", decisionId), ("$v", decision));
        return pending with { State = "deciding", Decision = decision };
    }
    public PendingPermission DecidePermission(string decisionId, long generation, string requestId, string turnId, string decision) { lock (_databaseGate) { var pending = BeginPermissionDecisionLocked(decisionId, generation, requestId, turnId, decision); if (pending.State == "decided") return pending; CompletePermissionDecisionLocked(decisionId); return pending with { State = "decided" }; } }
    public void CompletePermissionDecision(string decisionId) { lock (_databaseGate) CompletePermissionDecisionLocked(decisionId); }
    private void CompletePermissionDecisionLocked(string decisionId) => Execute("UPDATE pending_permissions SET state='decided' WHERE decision_id=$d AND state='deciding'", null, ("$d", decisionId));
    public void MarkPermissionUncertain(string decisionId) { lock (_databaseGate) Execute("UPDATE pending_permissions SET state='uncertain' WHERE decision_id=$d AND state='deciding'", null, ("$d", decisionId)); }

    public WorkerStatus Status()
    {
        lock (_databaseGate) return StatusLocked();
    }

    private WorkerStatus StatusLocked()
    {
        string processState;
        string? lifecycleHandle;
        long? observedPid;
        string? sessionId;
        string? activeRequestId;
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = "SELECT state,lifecycle_handle,pid,session_id,active_request_id FROM process_slot WHERE singleton=1";
            using var reader = command.ExecuteReader(); reader.Read();
            processState = reader.GetString(0);
            lifecycleHandle = reader.IsDBNull(1) ? null : reader.GetString(1);
            observedPid = reader.IsDBNull(2) ? null : reader.GetInt64(2);
            sessionId = reader.IsDBNull(3) ? null : reader.GetString(3);
            activeRequestId = reader.IsDBNull(4) ? null : reader.GetString(4);
        }
        var epoch = Convert.ToInt64(Scalar("SELECT epoch FROM lease WHERE singleton=1"), CultureInfo.InvariantCulture);
        var active = Convert.ToInt64(Scalar("SELECT active FROM lease WHERE singleton=1"), CultureInfo.InvariantCulture) == 1 && _clock.MonotonicMilliseconds <= _leaseDeadline;
        var held = Convert.ToInt64(Scalar("SELECT held FROM holds WHERE name='dispatch'"), CultureInfo.InvariantCulture) == 1;
        var reason = Scalar("SELECT reason FROM holds WHERE name='dispatch'")?.ToString(); var last = MetaLongLocked("next_event_sequence") - 1;
        var workerGeneration = MetaLongLocked("worker_generation");
        var processGeneration = MetaLongLocked("process_generation");
        var firstObject = Scalar("SELECT MIN(sequence) FROM events WHERE worker_generation=$g", null, ("$g", workerGeneration)); var first = firstObject is null or DBNull ? last + 1 : Convert.ToInt64(firstObject, CultureInfo.InvariantCulture);
        var ackGeneration = Convert.ToInt64(Scalar("SELECT ack_worker_generation FROM replay WHERE singleton=1"), CultureInfo.InvariantCulture);
        var ackSequence = Convert.ToInt64(Scalar("SELECT ack_sequence FROM replay WHERE singleton=1"), CultureInfo.InvariantCulture);
        return new WorkerStatus(workerGeneration, processGeneration, processState, lifecycleHandle, observedPid, sessionId, activeRequestId, CurrentPermissionLocked(), epoch, active, held, reason, first, last, ackGeneration, ackSequence);
    }

    public void SetHold(bool held, string? reason)
    {
        lock (_databaseGate)
        {
            if (reason is { Length: > 128 }) throw new WorkerProtocolException("Hold reason is too long.");
            if (!held && StatusLocked().HoldReason is { } current && (current.StartsWith("replay-gap:", StringComparison.Ordinal) || current is "replay-overflow" or "process-exited"))
                throw new WorkerProtocolException("This safety hold requires reconciliation or process recovery and cannot be cleared manually.");
            SetHoldInternal(held, reason);
        }
    }
    private void SetHoldInternal(bool held, string? reason) => Execute("UPDATE holds SET held=$h,reason=$r WHERE name='dispatch'", null, ("$h", held ? 1 : 0), ("$r", reason));
    private WorkerProtocolException ReplayGap(string reason) { SetHoldInternal(true, "replay-gap:" + reason); return new WorkerProtocolException("Replay cursor is invalid."); }

    private PendingPermission? CurrentPermission() { lock (_databaseGate) return CurrentPermissionLocked(); }
    private PendingPermission? CurrentPermissionLocked() { using var command = _connection.CreateCommand(); command.CommandText = "SELECT process_generation,request_id,turn_id,decision_id,payload_hash,option_ids_json,state,decision FROM pending_permissions WHERE state IN('pending','deciding','uncertain') ORDER BY created_utc LIMIT 1"; using var reader = command.ExecuteReader(); return !reader.Read() ? null : ReadPermission(reader); }
    private PendingPermission? GetPermission(string id) { lock (_databaseGate) return GetPermissionLocked(id); }
    private PendingPermission? GetPermissionLocked(string id) { using var command = _connection.CreateCommand(); command.CommandText = "SELECT process_generation,request_id,turn_id,decision_id,payload_hash,option_ids_json,state,decision FROM pending_permissions WHERE decision_id=$d"; command.Parameters.AddWithValue("$d", id); using var reader = command.ExecuteReader(); return !reader.Read() ? null : ReadPermission(reader); }
    private static PendingPermission ReadPermission(SqliteDataReader reader) => new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), JsonSerializer.Deserialize<string[]>(reader.GetString(5), WorkerProtocol.JsonOptions)!, reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7));
    private StoredRequest? QueryRequest(string id, SqliteTransaction? tx) { lock (_databaseGate) return QueryRequestLocked(id, tx); }
    private StoredRequest? QueryRequestLocked(string id, SqliteTransaction? tx) { using var command = _connection.CreateCommand(); command.Transaction = tx; command.CommandText = "SELECT payload_hash,state,outcome_json,process_generation,turn_id FROM requests WHERE request_id=$id"; command.Parameters.AddWithValue("$id", id); using var reader = command.ExecuteReader(); return !reader.Read() ? null : new(id, reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetInt64(3), reader.IsDBNull(4) ? null : reader.GetString(4)); }
    private StoredCancellation? QueryCancellation(string id, SqliteTransaction? tx) { lock (_databaseGate) return QueryCancellationLocked(id, tx); }
    private StoredCancellation? QueryCancellationLocked(string id, SqliteTransaction? tx) { using var command = _connection.CreateCommand(); command.Transaction = tx; command.CommandText = "SELECT target_request_id,payload_hash,state,process_generation FROM cancellations WHERE cancellation_id=$id"; command.Parameters.AddWithValue("$id", id); using var reader = command.ExecuteReader(); return !reader.Read() ? null : new(id, reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3)); }
    private void IncrementWorkerGenerationAndReconcileStartup()
    {
        using var tx = _connection.BeginTransaction();
        Execute("UPDATE meta SET value=CAST(value AS INTEGER)+1 WHERE key='worker_generation'; INSERT INTO event_generations(worker_generation,last_sequence) VALUES((SELECT CAST(value AS INTEGER) FROM meta WHERE key='worker_generation'),0); UPDATE meta SET value='1' WHERE key='next_event_sequence'; UPDATE lease SET active=0 WHERE singleton=1; UPDATE pending_permissions SET state='invalidated' WHERE state IN('pending','deciding'); UPDATE requests SET state='uncertain' WHERE state IN('forwarding','forwarded'); UPDATE cancellations SET state='uncertain' WHERE state IN('persisted','forwarding'); UPDATE process_slot SET state='exited',lifecycle_handle=NULL,session_id=NULL,active_request_id=NULL,pid=NULL,updated_utc=$u WHERE singleton=1 AND state IN('starting','running','uncertain'); UPDATE holds SET held=1,reason='process-exited' WHERE name='dispatch' AND EXISTS(SELECT 1 FROM process_slot WHERE singleton=1 AND state='exited')", tx, ("$u", Now()));
        tx.Commit();
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
