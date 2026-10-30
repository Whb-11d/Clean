using Clean.App;

namespace Clean.App.Tests;

public sealed class FormatSizeTests
{
    [Theory]
    [InlineData(0, 0, "B")]
    [InlineData(999, 999, "B")]
    [InlineData(1000, 1, "KB")]
    [InlineData(1000000, 1, "MB")]
    [InlineData(1000000000, 1, "GB")]
    [InlineData(1073741824, 1.1, "GB")]
    [InlineData(-1000000000, -1, "GB")]
    [InlineData(1000000000000, 1, "TB")]
    public void UsesDecimalUnitsIncludingNegativeSpaceChanges(double bytes, double amount, string unit)
        => Assert.Equal($"{amount:N1} {unit}", FormatSize.Bytes(bytes));
}
