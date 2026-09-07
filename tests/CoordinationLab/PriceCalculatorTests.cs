using Xunit;

namespace HVO.AgentControl.CoordinationLab;

public sealed class PriceCalculatorTests
{
    [Fact]
    public void OneItemKeepsItsPrice()
    {
        Assert.Equal(12.50m, PriceCalculator.Total(12.50m, 1));
    }
}
