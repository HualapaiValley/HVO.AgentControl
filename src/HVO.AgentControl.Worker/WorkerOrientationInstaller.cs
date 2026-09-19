using System.Net.Sockets;
using System.Text.Json;

namespace HVO.AgentControl.Worker;

/// <summary>The confirmed result of one supervisor orientation write.</summary>
public sealed record OrientationInstallReceipt(string InstalledPath, string ContentHash);

/// <summary>The confirmed result of one supervisor orientation read.</summary>
public sealed record OrientationReadReceipt(string InstalledPath, string ContentHash, byte[] Content);

/// <summary>
/// Performs the privileged orientation file write. The bridge process runs as the
/// unprivileged bridge uid and must never write employee-owned state directly, so
/// the fixed PID 1 supervisor owns the write and this is the narrow seam the
/// runtime depends on.
/// </summary>
public interface IWorkerOrientationInstaller
{
    Task<OrientationInstallReceipt> InstallAsync(string assignmentId, string orientationVersion, string artifactFileName, byte[] content, string contentHash, CancellationToken cancellationToken);

    /// <summary>
    /// Reads back the exact installed artifact through the fixed supervisor. The
    /// bridge uid cannot read the 0600 employee-owned file, so the supervisor
    /// forks an employee child to read and hash it. The returned hash must equal
    /// the requested expectation or the read is rejected.
    /// </summary>
    Task<OrientationReadReceipt> ReadAsync(string assignmentId, string orientationVersion, string artifactFileName, string contentHash, CancellationToken cancellationToken);
}

/// <summary>
/// Talks to the fixed <c>/run/worker-supervisor.sock</c> operation
/// <c>orientation-install</c> using the same framed JSON control protocol as the
/// other supervisor operations. The supervisor validates every field again and
/// writes atomically as the employee owner; this class only relays the already
/// validated content and verifies the returned path and hash.
/// </summary>
public sealed class WorkerOrientationInstaller(string supervisorSocketPath = WorkerOrientationInstaller.SupervisorSocketPath) : IWorkerOrientationInstaller
{
    public const string SupervisorSocketPath = "/run/worker-supervisor.sock";

    public async Task<OrientationInstallReceipt> InstallAsync(string assignmentId, string orientationVersion, string artifactFileName, byte[] content, string contentHash, CancellationToken cancellationToken)
    {
        WorkerProtocol.ValidateIdentifier(assignmentId, WorkerProtocol.MaxIdentifierLength, "orientation assignment id");
        WorkerProtocol.ValidateOrientationVersion(orientationVersion);
        WorkerProtocol.ValidateOrientationFileName(artifactFileName);
        WorkerProtocol.ValidateOrientationContentHash(contentHash);
        if (content.Length > WorkerProtocol.MaxOrientationContentBytes || content.AsSpan().IndexOf((byte)0) >= 0) throw new WorkerProtocolException("Orientation content is invalid.");
        if (!string.Equals(WorkerProtocol.OrientationContentHash(content), contentHash, StringComparison.Ordinal)) throw new WorkerProtocolException("Orientation content hash does not match the content.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(supervisorSocketPath), timeout.Token).ConfigureAwait(false);
        using var stream = new NetworkStream(socket);
        await WorkerProtocol.WriteFrameAsync(stream, new
        {
            operation = "orientation-install",
            assignmentId,
            orientationVersion,
            artifactFileName,
            contentHash,
            content = Convert.ToBase64String(content),
        }, timeout.Token).ConfigureAwait(false);
        using var response = await WorkerProtocol.ReadFrameAsync(stream, timeout.Token).ConfigureAwait(false)
            ?? throw new WorkerProtocolException("The fixed supervisor closed before answering orientation-install.");
        var root = response.RootElement;
        if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
            throw new WorkerProtocolException("The fixed supervisor rejected orientation-install.");
        if (!root.TryGetProperty("installedPath", out var path) || path.ValueKind != JsonValueKind.String || path.GetString() is not { } installedPath
            || !root.TryGetProperty("contentHash", out var hash) || hash.ValueKind != JsonValueKind.String || hash.GetString() is not { } returnedHash)
            throw new WorkerProtocolException("The fixed supervisor orientation-install answer is incomplete.");
        var expectedPath = WorkerProtocol.OrientationRootDirectory + "/" + artifactFileName;
        if (!string.Equals(installedPath, expectedPath, StringComparison.Ordinal) || !string.Equals(returnedHash, contentHash, StringComparison.Ordinal))
            throw new WorkerProtocolException("The fixed supervisor orientation-install answer did not correlate with the request.");
        return new OrientationInstallReceipt(installedPath, returnedHash);
    }

    public async Task<OrientationReadReceipt> ReadAsync(string assignmentId, string orientationVersion, string artifactFileName, string contentHash, CancellationToken cancellationToken)
    {
        WorkerProtocol.ValidateIdentifier(assignmentId, WorkerProtocol.MaxIdentifierLength, "orientation assignment id");
        WorkerProtocol.ValidateOrientationVersion(orientationVersion);
        WorkerProtocol.ValidateOrientationFileName(artifactFileName);
        WorkerProtocol.ValidateOrientationContentHash(contentHash);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(supervisorSocketPath), timeout.Token).ConfigureAwait(false);
        using var stream = new NetworkStream(socket);
        await WorkerProtocol.WriteFrameAsync(stream, new
        {
            operation = "orientation-read",
            assignmentId,
            orientationVersion,
            artifactFileName,
            contentHash,
        }, timeout.Token).ConfigureAwait(false);
        using var response = await WorkerProtocol.ReadFrameAsync(stream, timeout.Token).ConfigureAwait(false)
            ?? throw new WorkerProtocolException("The fixed supervisor closed before answering orientation-read.");
        var root = response.RootElement;
        if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
            throw new WorkerProtocolException("The fixed supervisor rejected orientation-read.");
        if (!root.TryGetProperty("installedPath", out var path) || path.ValueKind != JsonValueKind.String || path.GetString() is not { } installedPath
            || !root.TryGetProperty("contentHash", out var hash) || hash.ValueKind != JsonValueKind.String || hash.GetString() is not { } returnedHash
            || !root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String || content.GetString() is not { } encoded)
            throw new WorkerProtocolException("The fixed supervisor orientation-read answer is incomplete.");
        var expectedPath = WorkerProtocol.OrientationRootDirectory + "/" + artifactFileName;
        if (!string.Equals(installedPath, expectedPath, StringComparison.Ordinal) || !string.Equals(returnedHash, contentHash, StringComparison.Ordinal))
            throw new WorkerProtocolException("The fixed supervisor orientation-read answer did not correlate with the request.");

        byte[] bytes;
        try { bytes = Convert.FromBase64String(encoded); }
        catch (FormatException exception) { throw new WorkerProtocolException("The fixed supervisor orientation-read content is not valid base64.", exception); }
        if (bytes.Length > WorkerProtocol.MaxOrientationContentBytes || bytes.AsSpan().IndexOf((byte)0) >= 0
            || !string.Equals(WorkerProtocol.OrientationContentHash(bytes), contentHash, StringComparison.Ordinal))
            throw new WorkerProtocolException("The fixed supervisor orientation-read content did not correlate with its hash.");
        return new OrientationReadReceipt(installedPath, returnedHash, bytes);
    }
}
