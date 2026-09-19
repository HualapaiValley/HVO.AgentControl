namespace HVO.AgentControl.RemoteWorker;

public static class RemoteWorkerServiceCollectionExtensions
{
    public static IServiceCollection AddRemoteWorkerControl(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<WorkerControlOptions>(configuration.GetSection(WorkerControlOptions.SectionName));
        services.AddSingleton<IRemoteWorkerOperations, ProcessRemoteWorkerOperations>();
        services.AddSingleton<IRemoteWorkerProvisioner, RemoteWorkerProvisionerAdapter>();
        services.AddSingleton<ProcessWorkerConnector>();
        services.AddSingleton<IWorkerConnector>(provider => provider.GetRequiredService<ProcessWorkerConnector>());
        services.AddSingleton<IRemoteTerminalConnector>(provider => provider.GetRequiredService<ProcessWorkerConnector>());
        services.AddSingleton<IWorkerBridgeSessionFactory, WorkerBridgeSessionFactory>();
        services.AddSingleton<IControllerClock, SystemControllerClock>();
        services.AddSingleton<IWorkerDelay, SystemWorkerDelay>();
        services.AddSingleton<WorkerConnectionManager>();
        services.AddSingleton<IRemoteTerminalRouter, RemoteTerminalRouter>();
        services.AddHostedService<WorkerConnectionHostedService>();
        services.AddSingleton<RemoteWorkerProvisioningCoordinator>();
        services.AddSingleton<IRemoteWorkerStatusProvider, RemoteWorkerStatusProvider>();
        services.AddSingleton<ExecutionHostRegistry>();
        services.AddSingleton<ProfileBuildCoordinator>();
        return services;
    }
}
