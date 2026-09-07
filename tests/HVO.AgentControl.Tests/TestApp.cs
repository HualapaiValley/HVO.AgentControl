using System.Net;
using System.Text.RegularExpressions;
using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.AgentControl.Tests;

public sealed class TestApp : WebApplicationFactory<Program>
{
    public string DataPath { get; }
    public string SecretPath { get; }
    public const string OwnerPassword = "test-owner-password-0123456789-abcdefgh";
    public TestApp(string? data = null, string? secrets = null)
    {
        DataPath = data ?? Path.Combine(Path.GetTempPath(), "hvo-tests-" + Guid.NewGuid().ToString("N"));
        SecretPath = secrets ?? Path.Combine(DataPath, "secrets");
        Directory.CreateDirectory(DataPath); Directory.CreateDirectory(SecretPath);
        File.WriteAllText(Path.Combine(SecretPath, "owner-password"), OwnerPassword);
    }
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            var coordinator = services.FirstOrDefault(x => x.ImplementationType == typeof(HVO.AgentControl.Services.CoordinatorService));
            if (coordinator is not null) services.Remove(coordinator);
        });
        builder.UseSetting("Control:DataDirectory", DataPath);
        builder.UseSetting("Control:SecretsDirectory", SecretPath);
        builder.UseSetting("Control:AllowInsecureLocalHttp", "true");
        builder.UseSetting("Control:PollMilliseconds", "150");
        builder.UseSetting("Logging:LogLevel:Default", "Warning");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Control:DataDirectory"] = DataPath,
            ["Control:SecretsDirectory"] = SecretPath,
            ["Control:AllowInsecureLocalHttp"] = "true",
            ["Control:PollMilliseconds"] = "150",
            ["Logging:LogLevel:Default"] = "Warning"
        }));
    }
    public ControlStore Store => Services.GetRequiredService<ControlStore>();
    public async Task<HttpClient> SignIn()
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("http://localhost") });
        var page = await client.GetStringAsync("/login");
        var token = Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var response = await client.PostAsync("/auth/login", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["password"] = OwnerPassword, ["__RequestVerificationToken"] = WebUtility.HtmlDecode(token) }));
        if (response.StatusCode != HttpStatusCode.Redirect || response.Headers.Location?.ToString() != "/") throw new InvalidOperationException("Test sign-in failed: " + response.StatusCode);
        using var csrf = System.Text.Json.JsonDocument.Parse(await client.GetStringAsync("/api/v1/csrf"));
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrf.RootElement.GetProperty("token").GetString());
        return client;
    }
    public static async Task Wait(Func<Task<bool>> predicate, string description, int seconds = 30)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (!await predicate())
        {
            if (timeout.IsCancellationRequested) throw new TimeoutException(description);
            await Task.Delay(150);
        }
    }
}
