using System.Globalization;
using System.Text.Json;

namespace HVO.AgentControl.Runtime;

/// <summary>
/// Correlates ACP JSON-RPC responses with their requests. Registrations are
/// made before the frame is written so a fast response cannot race the pending
/// table.
/// </summary>
public sealed class AcpRpcCorrelator
{
    private readonly object _gate = new();
    private readonly Dictionary<string, PendingCall> _pending = new(StringComparer.Ordinal);
    private readonly int _maxPending;
    private long _nextId;

    public AcpRpcCorrelator(int maxPending = 1024)
    {
        if (maxPending < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPending));
        }

        _maxPending = maxPending;
    }

    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count;
            }
        }
    }

    public PendingCall Register(string method, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        cancellationToken.ThrowIfCancellationRequested();

        var id = Interlocked.Increment(ref _nextId);
        var key = "n:" + id.ToString(CultureInfo.InvariantCulture);
        var call = new PendingCall(id, key, method, cancellationToken);

        lock (_gate)
        {
            if (_pending.Count >= _maxPending)
            {
                throw new InvalidOperationException($"Too many in-flight ACP requests (limit {_maxPending}).");
            }

            _pending[key] = call;
        }

        if (cancellationToken.CanBeCanceled)
        {
            call.Registration = cancellationToken.Register(
                static state =>
                {
                    var (correlator, call) = ((AcpRpcCorrelator, PendingCall))state!;
                    if (correlator.TryRemove(call.Key, out var removed))
                    {
                        removed.Completion.TrySetCanceled(call.CancellationToken);
                    }
                },
                (this, call));
        }

        return call;
    }

    public bool TryComplete(AcpEnvelope response)
    {
        if (response.IdKey is not { } key)
        {
            return false;
        }

        if (response.Error is { } error)
        {
            var code = error.TryGetProperty("code", out var codeElement) && codeElement.TryGetInt64(out var parsed)
                ? parsed
                : -32000L;
            var message = error.TryGetProperty("message", out var messageElement)
                ? messageElement.GetString() ?? "remote error"
                : "remote error";
            var data = error.TryGetProperty("data", out var dataElement) ? dataElement.Clone() : (JsonElement?)null;
            return TryFail(key, new AcpRemoteException(code, message, data));
        }

        return response.Result is { } result && TrySucceed(key, result.Clone());
    }

    public bool TrySucceed(string key, JsonElement result)
    {
        if (!TryRemove(key, out var call))
        {
            return false;
        }

        call.Registration.Dispose();
        return call.Completion.TrySetResult(result);
    }

    public bool TryFail(string key, Exception exception)
    {
        if (!TryRemove(key, out var call))
        {
            return false;
        }

        call.Registration.Dispose();
        return call.Completion.TrySetException(exception);
    }

    public void FailAll(Exception exception)
    {
        List<PendingCall> calls;
        lock (_gate)
        {
            calls = _pending.Values.ToList();
            _pending.Clear();
        }

        foreach (var call in calls)
        {
            call.Registration.Dispose();
            call.Completion.TrySetException(exception);
        }
    }

    private bool TryRemove(string key, out PendingCall call)
    {
        lock (_gate)
        {
            return _pending.Remove(key, out call!);
        }
    }

    public sealed class PendingCall
    {
        internal PendingCall(long id, string key, string method, CancellationToken cancellationToken)
        {
            Id = id;
            Key = key;
            Method = method;
            CancellationToken = cancellationToken;
            Completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public long Id { get; }

        public string Key { get; }

        public string Method { get; }

        public Task<JsonElement> Task => Completion.Task;

        internal CancellationToken CancellationToken { get; }

        internal TaskCompletionSource<JsonElement> Completion { get; }

        internal CancellationTokenRegistration Registration { get; set; }
    }
}
