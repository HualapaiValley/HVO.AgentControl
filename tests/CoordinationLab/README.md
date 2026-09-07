# Pricing coordination lab

This deliberately incomplete draft is a coordination exercise, not production pricing software. Review it against the contract below. Build/test success alone does not establish correctness.

- Unit price is a nonnegative decimal; quantity is a positive integer. Reject invalid inputs.
- Subtotal is unit price multiplied by quantity.
- Apply a ten-percent discount for quantity ten or greater.
- Round the final total to two decimal places using MidpointRounding.AwayFromZero.
- Shipping cost is computed from the discounted subtotal: reject a negative input, otherwise charge `4.95m` for a discounted subtotal below `50.00m` and `0.00m` at or above `50.00m`.

Pricing state is corrected: the subtotal multiplies unit price by quantity, the ten-percent discount applies from quantity ten, and shipping applies to the already-discounted subtotal without any recalculation of pricing or rounding of the threshold input.

Run `dotnet test tests/CoordinationLab/CoordinationLab.csproj --configuration Release`. Add regression tests for every identified defect. Review/fix work is limited to this directory, and the native reviewer must name the exact reviewed commit. Do not merge or modify the AgentControl runtime during this exercise.
