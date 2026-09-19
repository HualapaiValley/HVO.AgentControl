using System.Globalization;
using System.Text.Json;
using HVO.AgentControl.Organization;

namespace HVO.AgentControl.RemoteWorker;

/// <summary>
/// Parses the real output of the fixed host probe commands. Every value comes
/// from what the daemon actually reported: <c>docker system info</c>,
/// <c>docker version</c> and one <c>df</c> of the daemon's own root directory.
/// </summary>
/// <remarks>
/// Failure separation is deliberate. Output that cannot be parsed at all is a
/// transport/protocol failure and throws <see cref="RemoteWorkerUnavailableException"/>,
/// because the controller learned nothing. Output that parses but does not prove
/// a required capability yields an <c>invalid</c> capability status: that is a
/// truthful observation about the host, not a controller fault, so it is recorded
/// rather than thrown.
/// </remarks>
public static class HostProbeParser
{
    /// <summary>Storage drivers whose volumes are known to be node-local.</summary>
    private static readonly HashSet<string> LocalStorageDrivers = new(StringComparer.OrdinalIgnoreCase)
    { "overlay2", "overlayfs", "btrfs", "zfs", "fuse-overlayfs", "vfs" };

    /// <summary>The only volume plugin that is proven node-local; anything else fails closed as shared.</summary>
    private const string LocalVolumePlugin = "local";

    private const long MinimumFreeBytes = 1024L * 1024 * 1024;
    private const long MinimumMemoryBytes = 512L * 1024 * 1024;

    /// <summary>Reads the daemon's own root directory from a real <c>docker system info</c> document.</summary>
    public static string ReadDockerRootDirectory(string infoJson)
    {
        using var document = Parse(infoJson, "host information");
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("DockerRootDir", out var value) || value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 } path)
            throw new RemoteWorkerUnavailableException("The host did not report its Docker root directory.");
        // Re-validated here and again at command construction: the path is remote
        // input, so it is only ever used after it matches the fixed absolute shape.
        // A path the host reports that this controller will not use is a property of
        // the host, not of the controller configuration.
        try { _ = RemoteWorkerCommandBuilder.QuoteAbsolutePath(path); }
        catch (WorkerControlConfigurationException exception) { throw new RemoteWorkerUnavailableException("The host reported a Docker root directory that is not a fixed absolute path.", transport: false, exception); }
        return path;
    }

    /// <summary>Parses the exact <c>df -B1 --output=avail</c> output: a header line and one byte count.</summary>
    public static long ParseAvailableBytes(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim()).Where(line => line.Length > 0).ToArray();
        if (lines.Length != 2 || !lines[0].Equals("Avail", StringComparison.Ordinal) || !long.TryParse(lines[1], NumberStyles.None, CultureInfo.InvariantCulture, out var available) || available < 0)
            throw new RemoteWorkerUnavailableException("The host did not report usable storage capacity.");
        return available;
    }

    /// <summary>
    /// Builds the durable capability record from the assembled probe payload and
    /// the controller-side known_hosts entry.
    /// </summary>
    public static ExecutionHostProbe Parse(HostProbePayload payload, ApprovedExecutionHost host, string expectedPlatform, int? expectedUid = null)
    {
        using var infoDocument = Parse(payload.InfoJson, "host information");
        using var versionDocument = Parse(payload.VersionJson, "daemon version");
        var info = infoDocument.RootElement;
        var version = versionDocument.RootElement;
        if (info.ValueKind != JsonValueKind.Object || version.ValueKind != JsonValueKind.Object) throw new RemoteWorkerUnavailableException("The host probe returned an unexpected document shape.");

        // docker version reports the image-compatible pair (linux/amd64); docker
        // info reports the uname machine string (x86_64), which is not a platform.
        var os = Text(version, "Os") ?? Text(info, "OSType");
        var architecture = Text(version, "Arch");
        var platform = os is not null && architecture is not null ? $"{os}/{architecture}" : "unknown";
        var serverVersion = Text(version, "Version") ?? Text(info, "ServerVersion");
        var apiVersion = Text(version, "ApiVersion");
        var driver = Text(info, "Driver");
        var backingFilesystem = ReadDriverStatus(info, "Backing Filesystem");
        var memory = Number(info, "MemTotal");
        var cpu = Number(info, "NCPU");
        var volumePlugins = ReadVolumePlugins(info);

        // Fail closed: storage is only treated as node-local when the driver is a
        // known local driver and no non-local volume plugin is installed.
        var localDriver = driver is not null && LocalStorageDrivers.Contains(driver);
        var localPluginsOnly = volumePlugins is not null && volumePlugins.Count > 0 && volumePlugins.All(plugin => plugin.Equals(LocalVolumePlugin, StringComparison.OrdinalIgnoreCase));
        var sharedStorage = !(localDriver && localPluginsOnly);

        // Limits must all be proven present; an absent field is not an allowance.
        var limits = Flag(info, "MemoryLimit") == true && Flag(info, "CpuCfsQuota") == true && Flag(info, "PidsLimit") == true;

        var mandatoryKnown = serverVersion is not null && apiVersion is not null && os is not null && architecture is not null && driver is not null && memory is not null && cpu is not null && volumePlugins is not null;
        var valid = mandatoryKnown
            && os == "linux"
            && architecture is "amd64" or "arm64"
            && platform == expectedPlatform
            && !sharedStorage
            && limits
            && payload.FreeBytes >= MinimumFreeBytes
            && memory >= MinimumMemoryBytes
            && cpu > 0;

        // The known_hosts entry is controller-side configuration, so an unusable one
        // is a configuration fault rather than a host observation.
        KnownHostIdentity knownHost;
        try { knownHost = KnownHostsParser.Parse(host, expectedUid ?? ControllerPrivateFile.EffectiveUid); }
        catch (InvalidOperationException exception) { throw new WorkerControlConfigurationException("The approved host known_hosts entry is not usable.", exception); }

        return new ExecutionHostProbe(
            knownHost.Algorithm,
            knownHost.Fingerprint,
            knownHost.ContentHash,
            serverVersion ?? "unknown",
            apiVersion ?? "unknown",
            architecture ?? "unknown",
            driver ?? "unknown",
            backingFilesystem ?? "unknown",
            sharedStorage,
            payload.FreeBytes,
            memory ?? 0,
            cpu is { } count && count is > 0 and <= int.MaxValue ? checked((int)count) : 1,
            limits,
            platform,
            valid ? "valid" : "invalid");
    }

    /// <summary>Builds the local capability record without any SSH host-key fields.</summary>
    public static LocalExecutionHostProbe ParseLocal(HostProbePayload payload, string expectedPlatform)
    {
        var parsed = ParseCapabilities(payload, expectedPlatform);
        return new LocalExecutionHostProbe(parsed.ServerVersion, parsed.ApiVersion, parsed.Architecture, parsed.Driver, parsed.BackingFilesystem, parsed.SharedStorage, payload.FreeBytes, parsed.Memory, parsed.Cpu, parsed.Limits, parsed.Platform, parsed.Valid ? "valid" : "invalid");
    }

    private static (string ServerVersion, string ApiVersion, string Architecture, string Driver, string BackingFilesystem, bool SharedStorage, long Memory, int Cpu, bool Limits, string Platform, bool Valid) ParseCapabilities(HostProbePayload payload, string expectedPlatform)
    {
        using var infoDocument = Parse(payload.InfoJson, "host information");
        using var versionDocument = Parse(payload.VersionJson, "daemon version");
        var info = infoDocument.RootElement;
        var version = versionDocument.RootElement;
        if (info.ValueKind != JsonValueKind.Object || version.ValueKind != JsonValueKind.Object) throw new RemoteWorkerUnavailableException("The host probe returned an unexpected document shape.");
        var os = Text(version, "Os") ?? Text(info, "OSType");
        var architecture = Text(version, "Arch");
        var platform = os is not null && architecture is not null ? $"{os}/{architecture}" : "unknown";
        var serverVersion = Text(version, "Version") ?? Text(info, "ServerVersion");
        var apiVersion = Text(version, "ApiVersion");
        var driver = Text(info, "Driver");
        var backingFilesystem = ReadDriverStatus(info, "Backing Filesystem");
        var memory = Number(info, "MemTotal");
        var cpu = Number(info, "NCPU");
        var volumePlugins = ReadVolumePlugins(info);
        var sharedStorage = !(driver is not null && LocalStorageDrivers.Contains(driver) && volumePlugins is { Count: > 0 } && volumePlugins.All(plugin => plugin.Equals(LocalVolumePlugin, StringComparison.OrdinalIgnoreCase)));
        var limits = Flag(info, "MemoryLimit") == true && Flag(info, "CpuCfsQuota") == true && Flag(info, "PidsLimit") == true;
        var valid = serverVersion is not null && apiVersion is not null && os == "linux" && architecture is "amd64" or "arm64" && platform == expectedPlatform && driver is not null && volumePlugins is not null && !sharedStorage && limits && payload.FreeBytes >= MinimumFreeBytes && memory >= MinimumMemoryBytes && cpu > 0;
        return (serverVersion ?? "unknown", apiVersion ?? "unknown", architecture ?? "unknown", driver ?? "unknown", backingFilesystem ?? "unknown", sharedStorage, memory ?? 0, cpu is { } count && count is > 0 and <= int.MaxValue ? checked((int)count) : 1, limits, platform, valid);
    }

    private static JsonDocument Parse(string json, string what)
    {
        if (json.Length is 0 or > 1024 * 1024) throw new RemoteWorkerUnavailableException($"The host probe returned no usable {what}.");
        try { return JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 }); }
        catch (JsonException exception) { throw new RemoteWorkerUnavailableException($"The host probe returned unparsable {what}.", transport: false, exception); }
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 and <= 128 } text && !text.Any(char.IsControl) ? text : null;

    private static long? Number(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) && number >= 0 ? number : null;

    private static bool? Flag(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    /// <summary>
    /// DriverStatus is an array of [key, value] pairs whose contents depend on the
    /// storage driver; the containerd snapshotter reports no backing filesystem at
    /// all, so an absent entry is expected rather than a failure.
    /// </summary>
    private static string? ReadDriverStatus(JsonElement root, string key)
    {
        if (!root.TryGetProperty("DriverStatus", out var status) || status.ValueKind != JsonValueKind.Array) return null;
        foreach (var pair in status.EnumerateArray())
        {
            if (pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() != 2) continue;
            var name = pair[0]; var value = pair[1];
            if (name.ValueKind != JsonValueKind.String || value.ValueKind != JsonValueKind.String) continue;
            if (!string.Equals(name.GetString(), key, StringComparison.OrdinalIgnoreCase)) continue;
            return value.GetString() is { Length: > 0 and <= 128 } text && !text.Any(char.IsControl) ? text : null;
        }
        return null;
    }

    /// <summary>Returns the installed volume plugin names, or null when the host did not report them.</summary>
    private static IReadOnlyList<string>? ReadVolumePlugins(JsonElement root)
    {
        if (!root.TryGetProperty("Plugins", out var plugins) || plugins.ValueKind != JsonValueKind.Object) return null;
        if (!plugins.TryGetProperty("Volume", out var volume) || volume.ValueKind != JsonValueKind.Array) return null;
        var names = new List<string>();
        foreach (var item in volume.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { Length: > 0 and <= 128 } text) return null;
            names.Add(text);
        }
        return names;
    }
}
