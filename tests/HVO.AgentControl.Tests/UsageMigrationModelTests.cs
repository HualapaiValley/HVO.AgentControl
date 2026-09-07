using HVO.AgentControl.Core;
using HVO.AgentControl.Infrastructure.Migrations;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class UsageMigrationModelTests
{
    [Fact]
    public void DurableUsageMigrationRetainsPreexistingOperatorEntities()
    {
        var model = new DurableModelUsage().TargetModel;

        Assert.NotNull(model.FindEntityType(typeof(ModelUsageRecord)));
        Assert.NotNull(model.FindEntityType(typeof(OperatorStatusUpdate)));
        Assert.NotNull(model.FindEntityType(typeof(OperatorUpdateSchedule)));
    }
}
