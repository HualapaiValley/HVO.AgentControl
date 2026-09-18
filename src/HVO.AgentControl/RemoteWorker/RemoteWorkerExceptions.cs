namespace HVO.AgentControl.RemoteWorker;

/// <summary>
/// Typed remote-worker failures. Each one carries the exact operator meaning the
/// API contract maps to a status code, so no remote-worker endpoint has to infer
/// intent from a bare <see cref="InvalidOperationException"/>.
/// </summary>
public abstract class RemoteWorkerException : Exception
{
    protected RemoteWorkerException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Worker control is switched off; nothing was executed. Maps to 409.</summary>
public sealed class WorkerControlDisabledException(string message = "Remote worker control is disabled.") : RemoteWorkerException(message);

/// <summary>Worker control is enabled but its configuration is unusable; nothing was executed. Maps to 409.</summary>
public sealed class WorkerControlConfigurationException(string message, Exception? inner = null) : RemoteWorkerException(message, inner);

/// <summary>
/// The remote host or its transport could not be reached or produced an
/// unusable answer. <see cref="Transport"/> distinguishes a connector/transport
/// failure (502) from a reachable-but-unavailable host (503).
/// </summary>
public sealed class RemoteWorkerUnavailableException(string message, bool transport = false, Exception? inner = null) : RemoteWorkerException(message, inner)
{
    public bool Transport { get; } = transport;
}

/// <summary>
/// An effect is uncertain or a durable obligation is open, so the operation is
/// refused until an operator reconciles it explicitly. Maps to 409.
/// </summary>
public sealed class WorkerRecoveryRequiredException(string message, string kind) : RemoteWorkerException(message)
{
    public string Kind { get; } = kind;
}

/// <summary>
/// The worker's exact offered option IDs contain no recognized safe rejection.
/// The pending permission remains held for compatibility remediation. Maps to 409.
/// </summary>
public sealed class WorkerPermissionOptionsUnsupportedException()
    : RemoteWorkerException("The worker offered no recognized reject permission option.");

/// <summary>A remote resource with the expected name is not exactly owned by this controller. Maps to 409.</summary>
public sealed class ForeignResourceException(string message) : RemoteWorkerException(message);
