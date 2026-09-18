using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using HVO.AgentControl.Runtime;
using Xunit;
using Xunit.Sdk;

namespace HVO.AgentControl.Tests;

/// <summary>
/// Wire-level proof using the real pinned OpenCode binary. Ordinary unit runs
/// skip when the binary is absent; pinned validation sets
/// AGENTCONTROL_OPENCODE_WIRE_REQUIRED=1 so absence, wrong version, timeout, or a
/// request-shape mismatch is a failure rather than a false green.
/// </summary>
[Collection(LocalPortBindingCollection.Name)]
public sealed class OpenCodeOutboundIntegrationTests
{
    private const string DisposableKey = "disposable-opencode-wire-key-243";

    [Fact]
    public async Task PinnedOpenCodeComposePrimaryProfileWorkhorseEmitsTerraMedium()
    {
        var required = string.Equals(
            Environment.GetEnvironmentVariable("AGENTCONTROL_OPENCODE_WIRE_REQUIRED"),
            "1",
            StringComparison.Ordinal);
        var executable = Environment.GetEnvironmentVariable("AGENTCONTROL_OPENCODE_EXECUTABLE") ?? "opencode";
        var version = await TryRunVersionAsync(executable);
        if (!required && !string.Equals(version, "1.18.30", StringComparison.Ordinal))
        {
            return; // The dedicated required CI check supplies and pins OpenCode 1.18.30.
        }

        Assert.Equal("1.18.30", version);
        using var root = new TemporaryDirectory();
        await using var endpoint = new FakeOpenAiEndpoint(DisposableKey);
        var secretPath = Path.Combine(root.Path, "cliproxy.key");
        var instructionsPath = Path.Combine(root.Path, "instructions.md");
        await File.WriteAllTextAsync(secretPath, DisposableKey + "\n");
        await File.WriteAllTextAsync(instructionsPath, "Reply with exactly OK and do not call tools.\n");

        var runtime = CliProxyRuntimeConfiguration.LoadRequired(new ControlOptions
        {
            Model = "cliproxy/default",
            ModelVariant = "medium",
            CliProxyEndpoint = endpoint.BaseUrl,
            CliProxySecretFile = secretPath,
        });
        var config = AgentControlOpenCodeConfig.Build(
            "cliproxy/default",
            instructionsPath,
            runtime);

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = root.Path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[]
        {
            "run", "--pure", "--format", "json", "--agent", "workhorse",
            "Reply with exactly OK and do not call tools.",
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["HOME"] = Path.Combine(root.Path, "home");
        startInfo.Environment["XDG_DATA_HOME"] = Path.Combine(root.Path, "data");
        startInfo.Environment["XDG_CONFIG_HOME"] = Path.Combine(root.Path, "config");
        startInfo.Environment["XDG_STATE_HOME"] = Path.Combine(root.Path, "state");
        startInfo.Environment["XDG_CACHE_HOME"] = Path.Combine(root.Path, "cache");
        startInfo.Environment["OPENCODE_CONFIG_CONTENT"] = config;
        startInfo.Environment[CliProxyModelCatalog.ApiKeyEnvironmentVariable] = DisposableKey;

        using var process = Process.Start(startInfo) ?? throw new XunitException("OpenCode did not start.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new XunitException(
                $"OpenCode did not complete within 45 seconds. stdout: {await standardOutput}\nstderr: {await standardError}");
        }

        var stdout = await standardOutput;
        var stderr = await standardError;
        Assert.True(process.ExitCode == 0, $"OpenCode exited {process.ExitCode}. stdout: {stdout}\nstderr: {stderr}");
        var request = await endpoint.Request.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("gpt-5.6-terra", request.GetProperty("model").GetString());
        Assert.Equal("medium", request.GetProperty("reasoning_effort").GetString());
        Assert.DoesNotContain(DisposableKey, stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(DisposableKey, stderr, StringComparison.Ordinal);
    }

    private static async Task<string?> TryRunVersionAsync(string executable)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (process is null)
            {
                return null;
            }
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(deadline.Token);
            return process.ExitCode == 0 ? (await process.StandardOutput.ReadToEndAsync()).Trim() : null;
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private sealed class FakeOpenAiEndpoint : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _lifetime = new();
        private readonly Task _server;
        private readonly string _key;
        private readonly TaskCompletionSource<JsonElement> _request = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeOpenAiEndpoint(string key)
        {
            _key = key;
            using var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            BaseUrl = $"http://127.0.0.1:{port}/v1";
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _server = ServeAsync();
        }

        public string BaseUrl { get; }
        public Task<JsonElement> Request => _request.Task;

        public async ValueTask DisposeAsync()
        {
            _lifetime.Cancel();
            _listener.Close();
            try
            {
                await _server;
            }
            catch (Exception exception) when (exception is HttpListenerException or OperationCanceledException or ObjectDisposedException)
            {
            }
            _lifetime.Dispose();
        }

        private async Task ServeAsync()
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var context = await _listener.GetContextAsync().WaitAsync(_lifetime.Token);
                var authorized = string.Equals(
                    context.Request.Headers["Authorization"],
                    $"Bearer {_key}",
                    StringComparison.Ordinal);
                if (!authorized)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    continue;
                }

                if (context.Request.HttpMethod == "GET" && context.Request.RawUrl == "/v1/models")
                {
                    await WriteJsonAsync(
                        context.Response,
                        "{\"object\":\"list\",\"data\":[{\"id\":\"default\"},{\"id\":\"gpt-5.6-terra\"}]}");
                    continue;
                }

                using var document = await JsonDocument.ParseAsync(context.Request.InputStream, cancellationToken: _lifetime.Token);
                _request.TrySetResult(document.RootElement.Clone());
                if (context.Request.RawUrl?.EndsWith("/responses", StringComparison.Ordinal) == true)
                {
                    await WriteEventStreamAsync(context.Response,
                    [
                        "{\"type\":\"response.created\",\"response\":{\"id\":\"resp_test\",\"object\":\"response\",\"created_at\":0,\"status\":\"in_progress\",\"model\":\"gpt-5.6-terra\",\"output\":[]}}",
                        "{\"type\":\"response.output_text.delta\",\"item_id\":\"msg_test\",\"output_index\":0,\"content_index\":0,\"delta\":\"OK\"}",
                        "{\"type\":\"response.completed\",\"response\":{\"id\":\"resp_test\",\"object\":\"response\",\"created_at\":0,\"status\":\"completed\",\"model\":\"gpt-5.6-terra\",\"output\":[{\"id\":\"msg_test\",\"type\":\"message\",\"status\":\"completed\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"OK\",\"annotations\":[]}]}],\"usage\":{\"input_tokens\":1,\"output_tokens\":1,\"total_tokens\":2}}}",
                    ]);
                }
                else
                {
                    await WriteEventStreamAsync(context.Response,
                    [
                        "{\"id\":\"chatcmpl_test\",\"object\":\"chat.completion.chunk\",\"created\":0,\"model\":\"gpt-5.6-terra\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"OK\"},\"finish_reason\":null}]}",
                        "{\"id\":\"chatcmpl_test\",\"object\":\"chat.completion.chunk\",\"created\":0,\"model\":\"gpt-5.6-terra\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}",
                    ]);
                }
            }
        }

        private static async Task WriteJsonAsync(HttpListenerResponse response, string body)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            response.StatusCode = 200;
            response.ContentType = "application/json";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes);
            response.Close();
        }

        private static async Task WriteEventStreamAsync(HttpListenerResponse response, IEnumerable<string> events)
        {
            response.StatusCode = 200;
            response.ContentType = "text/event-stream";
            response.SendChunked = true;
            foreach (var body in events)
            {
                var bytes = Encoding.UTF8.GetBytes($"data: {body}\n\n");
                await response.OutputStream.WriteAsync(bytes);
                await response.OutputStream.FlushAsync();
            }
            var done = Encoding.UTF8.GetBytes("data: [DONE]\n\n");
            await response.OutputStream.WriteAsync(done);
            response.Close();
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory() => Path = Directory.CreateTempSubdirectory("opencode-wire-243-").FullName;
        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
