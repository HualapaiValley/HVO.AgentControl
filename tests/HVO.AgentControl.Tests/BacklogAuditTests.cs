using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class BacklogAuditTests
{
    [Fact]
    public void GeneratedReportCannotBeUsedAsInputToScript()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var reportPath = Path.Combine(repoRoot, "docs", "backlog-audit-report.json");
        var scriptPath = Path.Combine(repoRoot, "scripts", "prioritize-backlog.py");

        Assert.True(File.Exists(reportPath), $"Generated report not found at {reportPath}");
        Assert.True(File.Exists(scriptPath), $"Script not found at {scriptPath}");

        var report = JsonDocument.Parse(File.ReadAllText(reportPath));
        Assert.True(report.RootElement.TryGetProperty("schemaVersion", out _),
            "Generated report has output schema (schemaVersion), not input schema (items)");

        var process = Process.Start(new ProcessStartInfo
        {
            FileName = "python3",
            Arguments = $"\"{scriptPath}\" --plan \"{reportPath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = repoRoot
        });
        Assert.NotNull(process);
        process.WaitForExit();

        Assert.NotEqual(0, process.ExitCode);
        var stderr = process.StandardError.ReadToEnd();
        Assert.Contains("KeyError", stderr);
    }

    [Fact]
    public void RealRepositoryPlanHasCorrectInputSchema()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var planPath = Path.Combine(repoRoot, "docs", "backlog-plan.json");

        Assert.True(File.Exists(planPath), $"Plan not found at {planPath}");

        var plan = JsonDocument.Parse(File.ReadAllText(planPath));
        var root = plan.RootElement;

        Assert.True(root.TryGetProperty("version", out _), "Plan missing 'version'");
        Assert.True(root.TryGetProperty("repository", out _), "Plan missing 'repository'");
        Assert.True(root.TryGetProperty("items", out var items), "Plan missing 'items'");
        Assert.Equal(JsonValueKind.Array, items.ValueKind);

        Assert.False(root.TryGetProperty("schemaVersion", out _), "Plan should not have output schema 'schemaVersion'");
        Assert.False(root.TryGetProperty("ready", out _), "Plan should not have output schema 'ready'");
        Assert.False(root.TryGetProperty("issues", out _), "Plan should not have output schema 'issues'");
    }
}
