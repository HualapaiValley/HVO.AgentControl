using Xunit;

namespace HVO.AgentControl.CoordinationLab;

public sealed class PriceCalculatorTests
{
    [Fact]
    public void OneItemKeepsItsPrice()
    {
        Assert.Equal(12.50m, PriceCalculator.Total(12.50m, 1));
    }

    [Fact]
    public void SubtotalMultipliesUnitPriceByQuantity()
    {
        Assert.Equal(62.50m, PriceCalculator.Total(12.50m, 5));
    }

    [Fact]
    public void TenPercentDiscountAppliesFromQuantityTen()
    {
        Assert.Equal(90.00m, PriceCalculator.Total(10.00m, 10));
    }

    [Fact]
    public void TenPercentDiscountAppliesToMultipliedSubtotal()
    {
        Assert.Equal(99.00m, PriceCalculator.Total(10.00m, 11));
    }

    public static TheoryData<decimal, int> NegativeUnitPrices()
        => new() { { -1.00m, 1 }, { -0.01m, 5 } };

    public static TheoryData<decimal, int> NonPositiveQuantities()
        => new() { { 10.00m, 0 }, { 10.00m, -1 }, { 10.00m, -5 } };

    [Theory]
    [MemberData(nameof(NegativeUnitPrices))]
    public void NegativeUnitPriceIsRejected(decimal unitPrice, int quantity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PriceCalculator.Total(unitPrice, quantity));
    }

    [Theory]
    [MemberData(nameof(NonPositiveQuantities))]
    public void NonPositiveQuantityIsRejected(decimal unitPrice, int quantity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PriceCalculator.Total(unitPrice, quantity));
    }

    [Fact]
    public void RoundsHalfwayAwayFromZero()
    {
        Assert.Equal(1.01m, PriceCalculator.Total(1.005m, 1));
    }

    [Fact]
    public void ShippingCostIsChargedBelowThreshold()
    {
        Assert.Equal(4.95m, PriceCalculator.ShippingCost(0.00m));
        Assert.Equal(4.95m, PriceCalculator.ShippingCost(49.99m));
    }

    [Fact]
    public void ShippingCostIsFreeAtAndAboveThreshold()
    {
        Assert.Equal(0.00m, PriceCalculator.ShippingCost(50.00m));
        Assert.Equal(0.00m, PriceCalculator.ShippingCost(50.01m));
    }

    [Fact]
    public void ShippingCostRejectsNegativeSubtotal()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PriceCalculator.ShippingCost(-0.01m));
    }
}
