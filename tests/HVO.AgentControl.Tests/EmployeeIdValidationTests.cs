using Xunit;

namespace HVO.AgentControl.Tests;

/// <summary>
/// The stable employee-id wire shape used by <c>/terminal</c> and
/// <c>/api/employees/{id}</c>. Generated ids are lower-case hex; persisted test
/// ids such as <c>emp-test</c> are also accepted.
/// </summary>
public sealed class EmployeeIdValidationTests
{
    [Theory]
    [InlineData("emp-test")]
    [InlineData("emp-does-not-exist")]
    [InlineData("emp-disabled")]
    [InlineData("emp-a")]
    [InlineData("emp-0")]
    [InlineData("emp-0123456789abcdef")]
    [InlineData("emp-x-y-z")]
    public void LowerCasePrefixedIdsAreAccepted(string value) =>
        Assert.True(Program.IsValidEmployeeId(value));

    [Fact]
    public void MaximumLengthIdIsAccepted()
    {
        var value = "emp-" + new string('a', Program.MaximumEmployeeIdLength - 4);

        Assert.Equal(Program.MaximumEmployeeIdLength, value.Length);
        Assert.True(Program.IsValidEmployeeId(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("emp-")]
    [InlineData("emp--")]
    [InlineData("emp")]
    [InlineData("employee-test")]
    [InlineData("EMP-test")]
    [InlineData("emp-Test")]
    [InlineData("emp-test/../other")]
    [InlineData("emp-test/sub")]
    [InlineData("emp_test")]
    [InlineData("emp-test ")]
    [InlineData(" emp-test")]
    public void MissingPrefixEmptySuffixUppercaseSlashOrPaddingAreRejected(string? value) =>
        Assert.False(Program.IsValidEmployeeId(value));

    [Fact]
    public void OverLengthIdIsRejected()
    {
        var value = "emp-" + new string('a', Program.MaximumEmployeeIdLength - 3);

        Assert.Equal(Program.MaximumEmployeeIdLength + 1, value.Length);
        Assert.False(Program.IsValidEmployeeId(value));
    }
}
