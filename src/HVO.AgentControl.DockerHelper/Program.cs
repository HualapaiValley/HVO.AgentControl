using HVO.AgentControl.DockerHelper;

namespace HVO.AgentControl.DockerHelperHost;

public static class DockerHelperProgram
{
    public static async Task Main()
    {
        var options = DockerHelperOptions.FromEnvironment();
        await using var server = new DockerHelperServer(options);
        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, args) => { args.Cancel = true; shutdown.Cancel(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => shutdown.Cancel();
        await server.RunAsync(shutdown.Token);
    }
}
