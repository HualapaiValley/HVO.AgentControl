namespace HVO.AgentControl.RemoteWorker;

public static class RemoteWorkerServiceCollectionExtensions
{
    public static IServiceCollection AddRemoteWorkerControl(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<WorkerControlOptions>(configuration.GetSection(WorkerControlOptions.SectionName));
        services.AddSingleton<LocalDockerHelperClient>();
        services.AddSingleton<ProcessRemoteWorkerOperations>();
        services.AddSingleton<ISshExecutionOperations>(provider => provider.GetRequiredService<ProcessRemoteWorkerOperations>());
        services.AddSingleton<LocalDockerExecutionOperations>();
        services.AddSingleton<ILocalDockerExecutionOperations>(provider => provider.GetRequiredService<LocalDockerExecutionOperations>());
        services.AddSingleton<IRemoteWorkerOperations, RoutingExecutionOperations>();
        services.AddSingleton<IRemoteWorkerProvisioner, RemoteWorkerProvisionerAdapter>();
        services.AddSingleton<ProcessWorkerConnector>();
        services.AddSingleton<RoutingWorkerConnector>();
        services.AddSingleton<IWorkerConnector>(provider => provider.GetRequiredService<RoutingWorkerConnector>());
        services.AddSingleton<IRemoteTerminalConnector>(provider => provider.GetRequiredService<RoutingWorkerConnector>());
        services.AddSingleton<IWorkerBridgeSessionFactory, WorkerBridgeSessionFactory>();
        services.AddSingleton<IControllerClock, SystemControllerClock>();
        services.AddSingleton<IWorkerDelay, SystemWorkerDelay>();
        services.AddSingleton<WorkerConnectionManager>();
        services.AddSingleton<IRemoteTerminalRouter, RemoteTerminalRouter>();
        services.AddHostedService<WorkerConnectionHostedService>();
        services.AddSingleton<RemoteWorkerProvisioningCoordinator>();
        services.AddSingleton<RemoteOrientationCoordinator>();
        services.AddSingleton<HireProvisioningCoordinator>();
        services.AddSingleton<EmployeeRebuildCoordinator>();
        services.AddSingleton<HireProvisioningHostedService>();
        services.AddHostedService(static services => services.GetRequiredService<HireProvisioningHostedService>());
        services.AddSingleton<IRemoteWorkerStatusProvider, RemoteWorkerStatusProvider>();
        services.AddSingleton<EmployeeTaskCoordinator>();
        services.AddSingleton<ExecutionHostRegistry>();
        services.AddSingleton<ProfileBuildCoordinator>();
        services.AddSingleton<CleanupCoordinator>();
        return services;
    }
}
