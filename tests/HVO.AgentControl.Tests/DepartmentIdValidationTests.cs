using Xunit;

namespace HVO.AgentControl.Tests;

/// <summary>
/// The stable department-id wire shape used by <c>/api/departments/{id}</c>.
/// Generated ids are lower-case hex; persisted test ids such as
/// <c>dept-test</c> are also accepted. The rule is the same bounded, prefix-bound
/// shape as the employee id so a path traversal or forged value is rejected
/// before any store read.
/// </summary>
public sealed class DepartmentIdValidationTests
{
    [Theory]
    [InlineData("dept-test")]
    [InlineData("dept-does-not-exist")]
    [InlineData("dept-a")]
    [InlineData("dept-0")]
    [InlineData("dept-0123456789abcdef")]
    [InlineData("dept-x-y-z")]
    public void LowerCasePrefixedIdsAreAccepted(string value) =>
        Assert.True(Program.IsValidDepartmentId(value));

    [Fact]
    public void MaximumLengthIdIsAccepted()
    {
        var value = "dept-" + new string('a', Program.MaximumDepartmentIdLength - 5);

        Assert.Equal(Program.MaximumDepartmentIdLength, value.Length);
        Assert.True(Program.IsValidDepartmentId(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("dept-")]
    [InlineData("dept--")]
    [InlineData("dept")]
    [InlineData("department-test")]
    [InlineData("DEPT-test")]
    [InlineData("dept-Test")]
    [InlineData("dept-test/../other")]
    [InlineData("dept-test/sub")]
    [InlineData("dept_test")]
    [InlineData("dept-test ")]
    [InlineData(" dept-test")]
    [InlineData("emp-test")]
    public void MissingPrefixEmptySuffixUppercaseSlashPaddingOrForeignPrefixAreRejected(string? value) =>
        Assert.False(Program.IsValidDepartmentId(value));

    [Fact]
    public void OverLengthIdIsRejected()
    {
        var value = "dept-" + new string('a', Program.MaximumDepartmentIdLength - 4);

        Assert.Equal(Program.MaximumDepartmentIdLength + 1, value.Length);
        Assert.False(Program.IsValidDepartmentId(value));
    }
}
