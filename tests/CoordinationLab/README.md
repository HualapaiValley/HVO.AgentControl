# Pricing coordination lab

This deliberately incomplete draft is a coordination exercise, not production pricing software. Review it against the contract below. Build/test success alone does not establish correctness.

- Unit price is a nonnegative decimal; quantity is a positive integer. Reject invalid inputs.
- Subtotal is unit price multiplied by quantity.
- Apply a ten-percent discount for quantity ten or greater.
- Round the final total to two decimal places using MidpointRounding.AwayFromZero.

Run `dotnet test tests/CoordinationLab/CoordinationLab.csproj --configuration Release`. Add regression tests for every identified defect. Review/fix work is limited to this directory, and the native reviewer must name the exact reviewed commit. Do not merge or modify the AgentControl runtime during this exercise.
