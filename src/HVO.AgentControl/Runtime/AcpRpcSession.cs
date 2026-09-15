using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Threading.Channels;

namespace HVO.AgentControl.Runtime;

/// <summary>Result the client returns for an inbound ACP request.</summary>
public sealed record AcpRequest(long Id, Task<JsonElement> Completion);

public sealed record AcpResponse
{
    public object? Result { get; init; }

    public int? ErrorCode { get; init; }

    public string? ErrorMessage { get; init; }

    public static AcpResponse Ok(object? result = null) => new() { Result = result };

    public static AcpResponse Error(int code, string message) => new() { ErrorCode = code, ErrorMessage = message };
}

/// <summary>
/// Reads and writes newline-delimited ACP JSON-RPC over a stream pair. The read
/// loop owns correlation resolution, notification delivery and inbound request
/// handling; outbound writes are serialized. It never inspects agent reasoning
/// content beyond routing frames.
/// </summary>
public sealed class AcpRpcSession
{
    private readonly Stream _input;
    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> _output;
    private readonly AcpJsonCodec _codec;
    private readonly AcpRpcCorrelator _correlator;
    private readonly Channel<JsonElement> _notifications;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private Task? _readLoop;

    public AcpRpcSession(
        Stream input,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> output,
        int maxFrameBytes = AcpJsonCodec.DefaultMaxFrameBytes,
        int maxPendingRequests = 1024,
        int notificationCapacity = 1024)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _codec = new AcpJsonCodec(input, maxFrameBytes);
        _correlator = new AcpRpcCorrelator(maxPendingRequests);
        _notifications = Channel.CreateBounded<JsonElement>(new BoundedChannelOptions(Math.Max(1, notificationCapacity))
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = false,
            SingleWriter = true,
        });
    }

    public ChannelReader<JsonElement> Notifications => _notifications.Reader;

    public Task Completion => _readLoop ?? throw new InvalidOperationException("The ACP session has not been started.");

    public Func<AcpEnvelope, CancellationToken, Task<AcpResponse>>? IncomingRequestHandler { get; set; }

    /// <summary>
    /// Optional non-blocking observer invoked synchronously in codec-reader order
    /// for every inbound frame, before a response can complete its correlator.
    /// The hook must copy any data it retains and must never perform blocking I/O.
    /// </summary>
    public Action<AcpEnvelope>? IncomingFrameHook { get; set; }

    public void Start()
    {
        if (_readLoop is not null)
        {
            throw new InvalidOperationException("The ACP session has already been started.");
        }

        _readLoop = Task.Run(RunReadLoopAsync);
    }

    public void RequestStop() => _lifetime.Cancel();

    public Task<JsonElement> RequestAsync(
        string method,
        object? parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        BeginRequest(method, parameters, timeout, cancellationToken).Completion;

    /// <summary>
    /// Registers a request and exposes its exact JSON-RPC id before any response
    /// can be correlated. The returned completion still owns write failures,
    /// cancellation and timeout cleanup.
    /// </summary>
    public AcpRequest BeginRequest(
        string method,
        object? parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Action<long>? registered = null)
    {
        var call = _correlator.Register(method, cancellationToken);
        try
        {
            registered?.Invoke(call.Id);
        }
        catch
        {
            _correlator.TryFail(call.Key, new AcpSessionClosedException("ACP request registration hook failed."));
            throw;
        }

        var completion = CompleteRequestAsync(call, method, parameters, timeout, cancellationToken);
        return new AcpRequest(call.Id, completion);
    }

    private async Task<JsonElement> CompleteRequestAsync(
        AcpRpcCorrelator.PendingCall call,
        string method,
        object? parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            await WriteAsync(
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = call.Id,
                    ["method"] = method,
                    ["params"] = parameters,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _correlator.TryFail(
                call.Key,
                exception is OperationCanceledException
                    ? exception
                    : new AcpSessionClosedException($"Failed to write ACP request '{method}'.", exception));
            throw;
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            return await call.Completion.Task.WaitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var timeoutException = new TimeoutException($"ACP request '{method}' timed out after {timeout}.");
            _correlator.TryFail(call.Key, timeoutException);
            throw timeoutException;
        }
    }

    public ValueTask NotifyAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        return WriteAsync(
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["jsonrpc"] = "2.0",
                ["method"] = method,
                ["params"] = parameters,
            },
            cancellationToken);
    }

    public ValueTask RespondAsync(AcpEnvelope request, object? result, CancellationToken cancellationToken)
    {
        if (request.Id is null)
        {
            return ValueTask.CompletedTask;
        }

        return WriteAsync(
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["jsonrpc"] = "2.0",
                ["id"] = request.Id.Value,
                ["result"] = result,
            },
            cancellationToken);
    }

    public ValueTask RespondErrorAsync(AcpEnvelope request, int code, string message, CancellationToken cancellationToken)
    {
        if (request.Id is null)
        {
            return ValueTask.CompletedTask;
        }

        return WriteAsync(
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["jsonrpc"] = "2.0",
                ["id"] = request.Id.Value,
                ["error"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = code,
                    ["message"] = message,
                },
            },
            cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        RequestStop();

        if (_readLoop is not null)
        {
            try
            {
                await _readLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // A non-cancellable pipe read may still be blocked; the caller is
                // expected to terminate the child process, which closes the pipe.
            }
            catch (OperationCanceledException)
            {
            }
            catch (AcpProtocolException)
            {
            }
            catch (IOException)
            {
                // The child pipe closed mid-read (process exit or teardown).
                // The read loop fault is preserved on Completion; disposal itself
                // must never surface an expected transport failure to the host.
            }
            catch (ObjectDisposedException)
            {
                // The pipe or stream was disposed underneath the read loop. As
                // above, the original fault stays observable on Completion.
            }
        }

        _notifications.Writer.TryComplete();
        _correlator.FailAll(new AcpSessionClosedException("ACP session disposed."));
    }

    private async Task RunReadLoopAsync()
    {
        Exception? failure = null;
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var envelope = await _codec.ReadAsync(_lifetime.Token).ConfigureAwait(false);
                if (envelope is null)
                {
                    break;
                }

                IncomingFrameHook?.Invoke(envelope);

                if (envelope.IsResponse)
                {
                    _correlator.TryComplete(envelope);
                }
                else if (envelope.IsNotification)
                {
                    if (envelope.Params is { } parameters)
                    {
                        _notifications.Writer.TryWrite(parameters.Clone());
                    }
                }
                else if (envelope.IsRequest && envelope.Id is not null)
                {
                    _ = HandleIncomingRequestAsync(envelope);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            _notifications.Writer.TryComplete(failure);
            _correlator.FailAll(failure ?? new AcpSessionClosedException("ACP session closed."));
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private async Task HandleIncomingRequestAsync(AcpEnvelope request)
    {
        AcpResponse response;
        var handler = IncomingRequestHandler;
        if (handler is null)
        {
            response = AcpResponse.Error(-32601, $"Method not supported: {request.Method}");
        }
        else
        {
            try
            {
                response = await handler(request, _lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                response = AcpResponse.Error(-32603, "Internal error while handling ACP request.");
            }
        }

        try
        {
            if (response.ErrorCode is { } code)
            {
                await RespondErrorAsync(request, code, response.ErrorMessage ?? "error", _lifetime.Token).ConfigureAwait(false);
            }
            else
            {
                await RespondAsync(request, response.Result, _lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // The connection is gone; the read loop will surface the failure.
        }
    }

    private async ValueTask WriteAsync(object payload, CancellationToken cancellationToken)
    {
        var frame = AcpJsonCodec.Encode(payload);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _output(frame, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
