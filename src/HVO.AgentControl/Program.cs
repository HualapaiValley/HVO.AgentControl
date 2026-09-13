var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/", () => Results.Ok(new
{
    name = "HVO.AgentControl",
    generation = 2,
    status = "baseline",
    workerControlImplemented = false
}));

app.MapGet("/health/live", () => Results.Ok(new { status = "healthy" }));
app.MapGet("/api/version", () => Results.Ok(new
{
    version = "2.0.0-alpha.1",
    agentHarness = "OpenCode",
    controlProtocol = "ACP",
    executionEnvironment = "Docker"
}));

app.Run();

public partial class Program;
