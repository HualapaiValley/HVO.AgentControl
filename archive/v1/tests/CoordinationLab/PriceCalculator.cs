namespace HVO.AgentControl.CoordinationLab;

public static class PriceCalculator
{
    public static decimal Total(decimal unitPrice, int quantity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(unitPrice);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);
        var subtotal = unitPrice * quantity;
        if (quantity >= 10) subtotal *= 0.90m;
        return Math.Round(subtotal, 2, MidpointRounding.AwayFromZero);
    }

    public static decimal ShippingCost(decimal discountedSubtotal)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(discountedSubtotal);
        return discountedSubtotal >= 50.00m ? 0.00m : 4.95m;
    }
}
