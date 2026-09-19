using System.Formats.Tar;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.AgentControl.Organization;

namespace HVO.AgentControl.RemoteWorker;

/// <summary>
/// Renders a validated container profile revision into a deterministic Docker
/// build context: one generated Dockerfile in a tar stream, nothing else. The
/// context is what the controller streams to <c>docker build -</c> on the
/// approved host, so everything a build can do is decided here, from content
/// the store already validated, and nothing is fetched from the network by
/// the renderer itself.
/// </summary>
/// <remarks>
/// <para>
/// The generated Dockerfile always has the same shape: <c>FROM</c> the
/// controller-owned pin tag of the exact approved worker base digest (BuildKit
/// does not accept an image id as a <c>FROM</c> source, so the build step first
/// tags the digest as <c>agentcontrol-worker-base:pin-&lt;12hex&gt;</c> and the
/// verifier later proves the built image's root filesystem starts with the
/// base's layers), the owner's
/// Dockerfile fragment body (already restricted to <c>RUN/ENV/ARG/LABEL/WORKDIR</c>),
/// the rendered devcontainer features as fixed <c>RUN</c> steps, the rendered
/// <c>containerEnv</c> as <c>ENV</c>, the lifecycle commands recorded as labels
/// (they are executed by the supervisor as the employee, never at build time),
/// and a fixed trailer that re-asserts the worker contract: setuid bits
/// stripped again, <c>/app</c> and the supervisor root-owned, the four private
/// directories 0700 with their fixed owners, the supervisor entrypoint. A
/// fragment cannot undo the trailer because it runs first.
/// </para>
/// <para>
/// Devcontainer features are not downloaded: each allowlisted feature id maps to
/// a fixed apt/installer recipe pinned by version, so the same revision renders
/// byte-identically on every host and the rendered text is part of the context
/// hash the build record stores.
/// </para>
/// </remarks>
public static class ProfileBuildContext
{
    public const string DockerfileName = "Dockerfile";
    public const string ProfileLabel = "agentcontrol.profile";
    public const string ContextHashLabel = "agentcontrol.context-hash";
    public const string BaseDigestLabel = "agentcontrol.base-digest";
    public const int MaximumDockerfileBytes = 64 * 1024;
    public const string PinRepository = "agentcontrol-worker-base";

    /// <summary>The controller-owned local tag under which the exact base digest is addressed by a build.</summary>
    public static string PinTag(string baseImageDigest)
    {
        RequireDigest(baseImageDigest);
        return PinRepository + ":pin-" + baseImageDigest.Substring(7, 12);
    }

    /// <summary>Feature id → apt/installer recipe. Only versions the recipe can honour are accepted.</summary>
    private static readonly IReadOnlyDictionary<string, FeatureRecipe> Recipes = new Dictionary<string, FeatureRecipe>(StringComparer.Ordinal)
    {
        // The base already carries the .NET 10 runtime; the SDK is what a developer needs.
        ["ghcr.io/devcontainers/features/dotnet:2"] = new(
            ["10.0", "latest"],
            _ => "RUN apt-get update && apt-get install -y --no-install-recommends dotnet-sdk-10.0 && rm -rf /var/lib/apt/lists/*"),
        // The base already carries node 22 (copied from the OpenCode stage); npm is present with it.
        ["ghcr.io/devcontainers/features/node:1"] = new(
            ["22", "lts", "latest"],
            _ => "RUN /usr/local/bin/node --version"),
        ["ghcr.io/devcontainers/features/python:1"] = new(
            ["3.12", "3", "latest"],
            _ => "RUN apt-get update && apt-get install -y --no-install-recommends python3 python3-venv python3-pip && rm -rf /var/lib/apt/lists/*"),
        ["ghcr.io/devcontainers/features/github-cli:1"] = new(
            ["latest"],
            _ => "RUN apt-get update && apt-get install -y --no-install-recommends gh && rm -rf /var/lib/apt/lists/*"),
    };

    private sealed record FeatureRecipe(IReadOnlyList<string> Versions, Func<string, string> Render);

    /// <summary>
    /// Renders the Dockerfile text for a revision against the exact base digest.
    /// Throws <see cref="OrganizationValidationException"/> for a feature version
    /// the fixed recipe cannot honour.
    /// </summary>
    public static string RenderDockerfile(ContainerProfileRevisionSummary revision, string baseImageDigest, string platform)
    {
        ArgumentNullException.ThrowIfNull(revision);
        RequireDigest(baseImageDigest);
        if (platform is not ("linux/amd64" or "linux/arm64")) throw new WorkerControlConfigurationException("Platform is invalid.");
        var definition = ContainerProfileDefinition.Parse(revision.Definition, revision.DockerfileFragment);
        if (!string.Equals(definition.ContentHash, revision.ContentHash, StringComparison.Ordinal))
            throw new OrganizationStoreCorruptException($"Container profile revision '{revision.Id}' content does not match its recorded hash.");

        var text = new StringBuilder();
        text.Append("# Generated by AgentControl for profile revision ").Append(revision.Id)
            .Append(" (").Append(revision.ContentHash).Append("). Do not edit.\n");
        text.Append("FROM ").Append(PinTag(baseImageDigest)).Append('\n');
        text.Append("LABEL ").Append(ProfileLabel).Append('=').Append(Quote(revision.Id)).Append(' ')
            .Append(BaseDigestLabel).Append('=').Append(Quote(baseImageDigest)).Append('\n');
        text.Append("USER root\n");

        if (definition.DockerfileFragment is { } fragment)
        {
            text.Append("# --- owner fragment ---\n");
            foreach (var line in fragment.Split('\n'))
            {
                if (line.Trim().StartsWith("FROM ", StringComparison.OrdinalIgnoreCase) || line.Trim().Equals("FROM", StringComparison.OrdinalIgnoreCase)) continue;
                if (line.Trim().StartsWith("from ", StringComparison.OrdinalIgnoreCase)) continue;
                text.Append(line).Append('\n');
            }
        }

        using (var document = JsonDocument.Parse(definition.CanonicalJson))
        {
            var root = document.RootElement;
            if (root.TryGetProperty("features", out var features))
            {
                text.Append("# --- features (fixed recipes) ---\n");
                foreach (var feature in features.EnumerateObject())
                {
                    var recipe = Recipes.TryGetValue(feature.Name, out var found) ? found
                        : throw new OrganizationValidationException($"Feature '{feature.Name}' has no fixed build recipe.");
                    var version = feature.Value.TryGetProperty("version", out var v) ? v.GetString() ?? "latest" : "latest";
                    if (!recipe.Versions.Contains(version, StringComparer.Ordinal))
                        throw new OrganizationValidationException($"Feature '{feature.Name}' version '{version}' is not one this build can honour ({string.Join(", ", recipe.Versions)}).");
                    text.Append("# ").Append(feature.Name).Append(" version=").Append(version).Append('\n');
                    text.Append(recipe.Render(version)).Append('\n');
                }
            }
            if (root.TryGetProperty("containerEnv", out var env))
            {
                text.Append("# --- containerEnv ---\n");
                foreach (var variable in env.EnumerateObject())
                    text.Append("ENV ").Append(variable.Name).Append('=').Append(Quote(variable.Value.GetString() ?? string.Empty)).Append('\n');
            }
            foreach (var (key, label) in new[] { ("postCreateCommand", "agentcontrol.post-create"), ("postStartCommand", "agentcontrol.post-start"), ("remoteEnv", "agentcontrol.remote-env") })
            {
                if (!root.TryGetProperty(key, out var value)) continue;
                // Recorded, not executed: the supervisor runs lifecycle commands as the
                // employee inside the running container; the image build never does.
                text.Append("LABEL ").Append(label).Append('=').Append(Quote(value.GetRawText())).Append('\n');
            }
        }

        text.Append("# --- worker contract trailer (fixed) ---\n");
        text.Append("RUN find / -xdev -perm /6000 -type f -exec chmod a-s {} + \\\n");
        text.Append("    && chown -R root:root /app /usr/local/bin/worker-supervisor \\\n");
        text.Append("    && chmod -R go-w /app && chmod 0755 /usr/local/bin/worker-supervisor \\\n");
        text.Append("    && chown 1101:1101 /control && chown 1102:1102 /home/worker /workspace /session \\\n");
        text.Append("    && chmod 0700 /control /home/worker /workspace /session \\\n");
        text.Append("    && test \"$(id -u bridge)\" = 1101 && test \"$(id -u employee)\" = 1102 \\\n");
        text.Append("    && test -x /usr/bin/dotnet && test -f /app/HVO.AgentControl.Worker.dll \\\n");
        text.Append("    && test -x /usr/local/bin/opencode && test -x /usr/bin/python3\n");
        text.Append("WORKDIR /workspace\n");
        text.Append("ENTRYPOINT [\"/usr/local/bin/worker-supervisor\"]\n");

        var rendered = text.ToString();
        if (Encoding.UTF8.GetByteCount(rendered) > MaximumDockerfileBytes)
            throw new OrganizationValidationException("The rendered Dockerfile exceeds the fixed size limit.");
        return rendered;
    }

    /// <summary>
    /// Produces the deterministic tar build context (a single <c>Dockerfile</c>
    /// entry with fixed metadata) and its SHA-256, which the build record stores as
    /// the context hash. Identical inputs produce identical bytes on every host.
    /// </summary>
    public static (byte[] Tar, string ContextHash, string Dockerfile) Render(ContainerProfileRevisionSummary revision, string baseImageDigest, string platform)
    {
        var dockerfile = RenderDockerfile(revision, baseImageDigest, platform);
        var bytes = Encoding.UTF8.GetBytes(dockerfile);
        using var stream = new MemoryStream();
        using (var writer = new TarWriter(stream, TarEntryFormat.Ustar, leaveOpen: true))
        {
            var entry = new UstarTarEntry(TarEntryType.RegularFile, DockerfileName)
            {
                Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
                Uid = 0,
                Gid = 0,
                UserName = "root",
                GroupName = "root",
                ModificationTime = DateTimeOffset.UnixEpoch,
                DataStream = new MemoryStream(bytes),
            };
            writer.WriteEntry(entry);
        }
        var tar = stream.ToArray();
        var hash = "sha256:" + Convert.ToHexString(SHA256.HashData(tar)).ToLowerInvariant();
        return (tar, hash, dockerfile);
    }

    private static string Quote(string value)
    {
        var escaped = value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
        return "\"" + escaped + "\"";
    }

    private static void RequireDigest(string digest)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(digest, "^sha256:[0-9a-f]{64}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            throw new WorkerControlConfigurationException("The base image must be an exact sha256 digest.");
    }

    public static string FormatBytes(long value) => value.ToString(CultureInfo.InvariantCulture);
}
