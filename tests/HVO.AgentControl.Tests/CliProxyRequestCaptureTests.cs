using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using HVO.AgentControl.Organization;
using HVO.AgentControl.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace HVO.AgentControl.Tests;

/// <summary>
/// End-to-end, disposable-key proof that a CLIProxy-configured control runtime
/// hands OpenCode exactly what it needs and nothing more.
/// </summary>
/// <remarks>
/// The checked-in <c>fake_opencode_capture.py</c> stands in for OpenCode. It
/// captures the real child environment and generated config into a file under
/// the test's private home and speaks a minimal ACP handshake; it makes no model
/// call and never prints the key. The assertions then inspect the captured
/// request mapping instead of relying on a live timeout.
/// </remarks>
[Collection(LocalPortBindingCollection.Name)]
public sealed class CliProxyRequestCaptureTests
{
    private const string DisposableKey = "disposable-capture-key-0000";

    [Fact]
    public async Task CliProxyChildGetsKeyAndReasoningEffortWhileConfigStaysSecretFree()
    {
        using var run = await RunCapturedHostAsync("cliproxy/gpt-5.6-terra", "medium");
        var data = run.DataDirectory;

        var capturePath = Path.Combine(data, "home", "opencode-capture.json");
        Assert.True(File.Exists(capturePath), "the fake OpenCode did not record its environment");
        using var capture = JsonDocument.Parse(File.ReadAllText(capturePath));

        // The disposable key is delivered only through the child environment.
        Assert.Equal(DisposableKey, capture.RootElement.GetProperty("apiKey").GetString());

        var configText = capture.RootElement.GetProperty("config").GetString();
        Assert.False(string.IsNullOrWhiteSpace(configText));
        Assert.DoesNotContain(DisposableKey, configText!, StringComparison.Ordinal);

        using var config = JsonDocument.Parse(configText!);
        var agent = config.RootElement.GetProperty("agent").GetProperty(AgentControlOpenCodeConfig.RoleName);

        // The requested alias and variant are explicit, not defaults.
        Assert.Equal("cliproxy/gpt-5.6-terra", agent.GetProperty("model").GetString());
        Assert.Equal("medium", agent.GetProperty("variant").GetString());

        // The advertised medium variant carries the actual reasoning effort, so
        // the provider request states it instead of an empty option object.
        var medium = config.RootElement
            .GetProperty("provider").GetProperty("cliproxy")
            .GetProperty("models").GetProperty("gpt-5.6-terra")
            .GetProperty("variants").GetProperty("medium");
        Assert.Equal("medium", medium.GetProperty("reasoningEffort").GetString());

        // ACP launched as the control role, not an arbitrary argv.
        Assert.Equal("acp", capture.RootElement.GetProperty("args")[0].GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, "selected", "configured", true)]
    [InlineData(HttpStatusCode.OK, "empty", "unavailable", false)]
    [InlineData(HttpStatusCode.OK, "missing", "unavailable", false)]
    [InlineData(HttpStatusCode.OK, "malformed", "unavailable", false)]
    [InlineData(HttpStatusCode.OK, "oversized", "unavailable", false)]
    [InlineData(HttpStatusCode.OK, "wrong-content", "unavailable", false)]
    [InlineData(HttpStatusCode.Unauthorized, "error", "revoked", false)]
    [InlineData(HttpStatusCode.Forbidden, "error", "revoked", false)]
    [InlineData(HttpStatusCode.InternalServerError, "error", "unavailable", false)]
    public async Task CatalogPreflightRecordsSanitizedStatusBeforeChildStart(
        HttpStatusCode responseStatus,
        string responseShape,
        string expectedStatus,
        bool childStarts)
    {
        using var dataRoot = new TemporaryDirectory("cliproxy-preflight-");
        using var executable = CreateCaptureExecutable();
        await using var catalog = await FakeCatalogEndpoint.StartAsync(DisposableKey, responseStatus, responseShape);
        var secretPath = Path.Combine(dataRoot.Path, "cliproxy.key");
        File.WriteAllText(secretPath, DisposableKey + "\n");
        var options = new ControlOptions
        {
            Enabled = true,
            DataDirectory = dataRoot.Path,
            OpenCodeExecutable = executable.Path,
            NativePort = GetFreePort(),
            EnableTerminal = false,
            StartupTimeoutSeconds = 15,
            PromptTimeoutSeconds = 15,
            Model = "cliproxy/default",
            ModelVariant = "medium",
            CliProxyEndpoint = catalog.BaseUrl,
            CliProxySecretFile = secretPath,
        };

        using var host = new AcpControlHost(Options.Create(options), NullLogger<AcpControlHost>.Instance);
        await host.StartAsync(CancellationToken.None);
        var desired = childStarts ? "ready" : "faulted";
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTimeOffset.UtcNow < deadline && host.GetStatus().State != desired)
        {
            await Task.Delay(100);
        }

        Assert.Equal(desired, host.GetStatus().State);
        var database = Path.Combine(dataRoot.Path, OrganizationStore.DatabaseFileName);
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={database};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT provider_config_status FROM runtime_bindings";
        Assert.Equal(expectedStatus, command.ExecuteScalar());
        Assert.Equal(childStarts, File.Exists(Path.Combine(dataRoot.Path, "home", "opencode-capture.json")));
        Assert.DoesNotContain(DisposableKey, host.GetStatus().Error ?? string.Empty, StringComparison.Ordinal);
        await host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task CatalogPreflightWithWrongKeyIsRevokedBeforeChildStart()
    {
        using var dataRoot = new TemporaryDirectory("cliproxy-preflight-wrong-key-");
        using var executable = CreateCaptureExecutable();
        await using var catalog = await FakeCatalogEndpoint.StartAsync("different-disposable-key", HttpStatusCode.OK, "selected");
        var secretPath = Path.Combine(dataRoot.Path, "cliproxy.key");
        File.WriteAllText(secretPath, DisposableKey);
        var options = new ControlOptions
        {
            Enabled = true,
            DataDirectory = dataRoot.Path,
            OpenCodeExecutable = executable.Path,
            NativePort = GetFreePort(),
            EnableTerminal = false,
            StartupTimeoutSeconds = 15,
            PromptTimeoutSeconds = 15,
            Model = "cliproxy/default",
            ModelVariant = "medium",
            CliProxyEndpoint = catalog.BaseUrl,
            CliProxySecretFile = secretPath,
        };

        using var host = new AcpControlHost(Options.Create(options), NullLogger<AcpControlHost>.Instance);
        await host.StartAsync(CancellationToken.None);
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTimeOffset.UtcNow < deadline && host.GetStatus().State != "faulted")
        {
            await Task.Delay(100);
        }

        Assert.Equal("faulted", host.GetStatus().State);
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={Path.Combine(dataRoot.Path, OrganizationStore.DatabaseFileName)};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT provider_config_status FROM runtime_bindings";
        Assert.Equal("revoked", command.ExecuteScalar());
        Assert.False(File.Exists(Path.Combine(dataRoot.Path, "home", "opencode-capture.json")));
        Assert.DoesNotContain(DisposableKey, host.GetStatus().Error ?? string.Empty, StringComparison.Ordinal);
        await host.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// A CLIProxy-required runtime with a missing secret must fault before any
    /// OpenCode process starts and must never substitute Big Pickle.
    /// </summary>
    [Fact]
    public async Task MissingCliProxySecretFaultsBeforeOpenCodeStartsWithoutFallback()
    {
        using var dataRoot = new TemporaryDirectory("cliproxy-capture-missing-");
        var data = dataRoot.Path;
        using var executable = CreateCaptureExecutable();
        var options = new ControlOptions
        {
            Enabled = true,
            DataDirectory = data,
            OpenCodeExecutable = executable.Path,
            NativePort = GetFreePort(),
            EnableTerminal = false,
            StartupTimeoutSeconds = 15,
            PromptTimeoutSeconds = 15,
            Model = "cliproxy/default",
            ModelVariant = "medium",
            CliProxyEndpoint = "http://127.0.0.1:8317/v1",
            CliProxySecretFile = Path.Combine(data, "definitely-missing.key"),
        };

        using var host = new AcpControlHost(Options.Create(options), NullLogger<AcpControlHost>.Instance);
        await host.StartAsync(CancellationToken.None);

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTimeOffset.UtcNow < deadline && host.GetStatus().State != "faulted")
        {
            await Task.Delay(100);
        }

        var status = host.GetStatus();
        Assert.Equal("faulted", status.State);
        Assert.Contains("secret", status.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("big-pickle", status.Error!, StringComparison.OrdinalIgnoreCase);

        // OpenCode never ran: no capture was written.
        Assert.False(File.Exists(Path.Combine(data, "home", "opencode-capture.json")));
        var database = Path.Combine(data, OrganizationStore.DatabaseFileName);
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={database};Mode=ReadOnly"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT provider_config_status FROM runtime_bindings";
            Assert.Equal("unavailable", command.ExecuteScalar());
        }

        await host.StopAsync(CancellationToken.None);
    }

    private static async Task<CapturedRun> RunCapturedHostAsync(string model, string variant)
    {
        var dataRoot = new TemporaryDirectory("cliproxy-capture-");
        var executable = CreateCaptureExecutable();
        var catalog = await FakeCatalogEndpoint.StartAsync(DisposableKey, HttpStatusCode.OK, "selected");
        try
        {
            var data = dataRoot.Path;
            var secretPath = Path.Combine(data, "cliproxy.key");
            File.WriteAllText(secretPath, DisposableKey);
            var options = new ControlOptions
            {
                Enabled = true,
                DataDirectory = data,
                OpenCodeExecutable = executable.Path,
                NativePort = GetFreePort(),
                EnableTerminal = false,
                StartupTimeoutSeconds = 15,
                PromptTimeoutSeconds = 15,
                Model = model,
                ModelVariant = variant,
                CliProxyEndpoint = catalog.BaseUrl,
                CliProxySecretFile = secretPath,
            };

            using var host = new AcpControlHost(Options.Create(options), NullLogger<AcpControlHost>.Instance);
            await host.StartAsync(CancellationToken.None);

            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
            while (DateTimeOffset.UtcNow < deadline && host.GetStatus().State != "ready")
            {
                var current = host.GetStatus();
                if (current.State == "faulted")
                {
                    throw new Xunit.Sdk.XunitException($"host faulted: {current.Error}");
                }

                await Task.Delay(100);
            }

            Assert.Equal("ready", host.GetStatus().State);
            await host.StopAsync(CancellationToken.None);
            return new CapturedRun(dataRoot, executable, catalog);
        }
        catch
        {
            await catalog.DisposeAsync();
            executable.Dispose();
            dataRoot.Dispose();
            throw;
        }
    }

    private static TemporaryExecutable CreateCaptureExecutable()
    {
        var canonical = Path.Combine(AppContext.BaseDirectory, "Fixtures", "fake_opencode_capture.py");
        Assert.True(File.Exists(canonical), $"canonical capture fixture missing at '{canonical}'");
        var directory = Directory.CreateTempSubdirectory("cliproxy-capture-bin-");
        var executable = Path.Combine(directory.FullName, "fake_opencode_capture.py");
        File.CreateSymbolicLink(executable, canonical);
        return new TemporaryExecutable(directory, executable);
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed record TemporaryExecutable(DirectoryInfo Directory, string Path) : IDisposable
    {
        public void Dispose() => Directory.Delete(recursive: true);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory(string prefix) => Path = Directory.CreateTempSubdirectory(prefix).FullName;

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private sealed record CapturedRun(
        TemporaryDirectory DataRoot,
        TemporaryExecutable Executable,
        FakeCatalogEndpoint Catalog) : IDisposable
    {
        public string DataDirectory => DataRoot.Path;

        public void Dispose()
        {
            Catalog.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Executable.Dispose();
            DataRoot.Dispose();
        }
    }

    private sealed class FakeCatalogEndpoint : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly Task _server;
        private readonly string _key;
        private readonly HttpStatusCode _status;
        private readonly string _responseShape;

        private FakeCatalogEndpoint(TcpListener listener, string key, HttpStatusCode status, string responseShape)
        {
            _listener = listener;
            _key = key;
            _status = status;
            _responseShape = responseShape;
            _server = ServeAsync();
        }

        public string BaseUrl => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/v1";

        public static Task<FakeCatalogEndpoint> StartAsync(string key, HttpStatusCode status, string responseShape)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return Task.FromResult(new FakeCatalogEndpoint(listener, key, status, responseShape));
        }

        public async ValueTask DisposeAsync()
        {
            _lifetime.Cancel();
            _listener.Stop();
            try
            {
                await _server;
            }
            catch (Exception exception) when (exception is OperationCanceledException or SocketException or IOException)
            {
            }
            _lifetime.Dispose();
        }

        private async Task ServeAsync()
        {
            while (!_lifetime.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(_lifetime.Token);
                using var stream = client.GetStream();
                using var reader = new StreamReader(
                    stream,
                    System.Text.Encoding.ASCII,
                    detectEncodingFromByteOrderMarks: false,
                    bufferSize: 1024,
                    leaveOpen: true);
                var requestLine = await reader.ReadLineAsync(_lifetime.Token);
                var authorized = false;
                string? line;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(_lifetime.Token)))
                {
                    authorized |= string.Equals(line, $"Authorization: Bearer {_key}", StringComparison.Ordinal);
                }

                var status = authorized ? _status : HttpStatusCode.Unauthorized;
                var body = status == HttpStatusCode.OK
                    ? _responseShape switch
                    {
                        "selected" => "{\"object\":\"list\",\"data\":[{\"id\":\"default\",\"object\":\"model\"},{\"id\":\"gpt-5.6-terra\",\"object\":\"model\"}]}",
                        "empty" => string.Empty,
                        "missing" => "{\"object\":\"list\",\"data\":[{\"id\":\"gpt-5.6-terra\"}]}",
                        "malformed" => "{not-json",
                        "oversized" => "{\"data\":[{\"id\":\"default\",\"padding\":\"" + new string('x', CliProxyRuntimeConfiguration.MaximumCatalogResponseBytes) + "\"}]}",
                        "wrong-content" => "<html>not a model catalog</html>",
                        _ => throw new InvalidOperationException($"unknown catalog response shape '{_responseShape}'"),
                    }
                    : "{\"error\":\"denied\"}";
                var bodyBytes = System.Text.Encoding.UTF8.GetBytes(body);
                var contentType = _responseShape == "wrong-content" ? "text/html" : "application/json";
                var responseHeaders = $"HTTP/1.1 {(int)status} {status}\r\nContent-Type: {contentType}\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n";
                Assert.Equal("GET /v1/models HTTP/1.1", requestLine);
                await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(responseHeaders), _lifetime.Token);
                await stream.WriteAsync(bodyBytes, _lifetime.Token);
            }
        }
    }
}
