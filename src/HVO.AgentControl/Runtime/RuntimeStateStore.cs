using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HVO.AgentControl.Runtime;

public sealed class RuntimeStateException : Exception
{
    public RuntimeStateException(string message)
        : base(message)
    {
    }

    public RuntimeStateException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Durable identity persisted at <c>DataDirectory/runtime.json</c>.</summary>
public sealed class PersistedRuntimeState
{
    [JsonPropertyName("organizationId")]
    public string OrganizationId { get; set; } = string.Empty;

    [JsonPropertyName("organizationName")]
    public string OrganizationName { get; set; } = string.Empty;

    [JsonPropertyName("sessionId")]
    public string? SessionId { get; set; }

    [JsonPropertyName("sessionTitle")]
    public string? SessionTitle { get; set; }

    [JsonPropertyName("tmuxOwnerToken")]
    public string? TmuxOwnerToken { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A parsed runtime state and the exact bytes it was parsed from.</summary>
public sealed record PersistedRuntimeEvidence(PersistedRuntimeState State, byte[] Bytes, string Path);

/// <summary>
/// Small durable store for the control runtime. Loading a malformed or
/// unreadable file throws <see cref="RuntimeStateException"/> so the host faults
/// instead of silently discarding recorded identity.
/// </summary>
public static class RuntimeStateStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>Returns the persisted state, or null when the file does not exist.</summary>
    public static PersistedRuntimeState? Load(string path)
    {
        return LoadEvidence(path)?.State;
    }

    /// <summary>Returns parsed state with its byte-exact source, or null when absent.</summary>
    public static PersistedRuntimeEvidence? LoadEvidence(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            return null;
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new RuntimeStateException($"Could not read runtime state '{path}'.", exception);
        }

        PersistedRuntimeState? state;
        try
        {
            state = JsonSerializer.Deserialize<PersistedRuntimeState>(bytes, SerializerOptions);
        }
        catch (JsonException exception)
        {
            throw new RuntimeStateException($"Runtime state '{path}' is not valid JSON.", exception);
        }

        if (state is null)
        {
            throw new RuntimeStateException($"Runtime state '{path}' deserialized to null.");
        }

        if (string.IsNullOrWhiteSpace(state.OrganizationId))
        {
            throw new RuntimeStateException($"Runtime state '{path}' is missing organizationId.");
        }

        return new PersistedRuntimeEvidence(state, bytes, Path.GetFullPath(path));
    }

    public static PersistedRuntimeState CreateNew(string organizationName, Func<string>? organizationIdFactory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationName);
        var now = DateTimeOffset.UtcNow;
        return new PersistedRuntimeState
        {
            OrganizationId = (organizationIdFactory ?? NewOrganizationId)(),
            OrganizationName = organizationName,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    public static void Save(string path, PersistedRuntimeState state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(state);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        state.UpdatedAt = DateTimeOffset.UtcNow;
        var json = JsonSerializer.Serialize(state, SerializerOptions);
        var temporaryPath = path + ".tmp";

        try
        {
            File.WriteAllText(temporaryPath, json);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temporaryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new RuntimeStateException($"Could not write runtime state '{path}'.", exception);
        }
    }

    public static string NewOrganizationId()
    {
        return "org-" + RandomNumberGenerator.GetHexString(16).ToLowerInvariant();
    }

    public static string NewOwnerToken()
    {
        return RandomNumberGenerator.GetHexString(24).ToLowerInvariant();
    }
}
