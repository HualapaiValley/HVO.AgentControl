using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure.Migrations;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class UsageMigrationModelTests
{
    [Theory]
    [InlineData(typeof(DurableModelUsage))]
    [InlineData(typeof(RuntimeTelemetryHistory))]
    [InlineData(typeof(GitHubCiReadPermissionEvidence))]
    public void PostUsageMigrationsRetainPreexistingUsageAndOperatorEntities(Type migrationType)
    {
        var instance = (dynamic)Activator.CreateInstance(migrationType)!;
        var model = instance.TargetModel;

        Assert.NotNull(model.FindEntityType(typeof(ModelUsageRecord)));
        Assert.NotNull(model.FindEntityType(typeof(OperatorStatusUpdate)));
        Assert.NotNull(model.FindEntityType(typeof(OperatorUpdateSchedule)));
    }
}
