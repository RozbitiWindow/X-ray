using XRay.Core.Formatting;

namespace XRay.Core.Tests;

public class SizeFormatterTests
{
    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1L, "1 B")]
    [InlineData(1023L, "1,023 B")]
    [InlineData(1024L, "1.00 KiB")]
    [InlineData(1536L, "1.50 KiB")]
    [InlineData(10L * 1024, "10.0 KiB")]
    [InlineData(153L * 1024, "153 KiB")]
    [InlineData(1024L * 1024, "1.00 MiB")]
    [InlineData(1024L * 1024 * 1024 * 3 / 2, "1.50 GiB")]
    [InlineData(1024L * 1024 * 1024 * 1024, "1.00 TiB")]
    public void Binary_formats_with_three_significant_digits(long bytes, string expected) =>
        Assert.Equal(expected, SizeFormatter.Format(bytes, SizeUnitMode.Binary));

    [Theory]
    [InlineData(999L, "999 B")]
    [InlineData(1000L, "1.00 kB")]
    [InlineData(1_500_000L, "1.50 MB")]
    [InlineData(250_000_000_000L, "250 GB")]
    public void Decimal_uses_powers_of_1000(long bytes, string expected) =>
        Assert.Equal(expected, SizeFormatter.Format(bytes, SizeUnitMode.Decimal));

    [Fact]
    public void Windows_mode_uses_1024_with_short_labels() =>
        Assert.Equal("1.00 GB", SizeFormatter.Format(1024L * 1024 * 1024, SizeUnitMode.Windows));

    [Fact]
    public void Rounding_promotes_to_next_unit_instead_of_showing_1024()
    {
        // 1023.9 KiB would otherwise display as "1024 KiB".
        Assert.Equal("1.00 MiB", SizeFormatter.Format(1024L * 1024 - 10, SizeUnitMode.Binary));
    }

    [Fact]
    public void Negative_values_keep_their_sign() =>
        Assert.Equal("-2.00 KiB", SizeFormatter.Format(-2048));

    [Fact]
    public void Extreme_values_do_not_throw()
    {
        Assert.EndsWith("EiB", SizeFormatter.Format(long.MaxValue));
        Assert.StartsWith("-", SizeFormatter.Format(long.MinValue));
    }

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(2048L, "+2.00 KiB")]
    [InlineData(-2048L, "-2.00 KiB")]
    public void Delta_has_explicit_sign(long delta, string expected) =>
        Assert.Equal(expected, SizeFormatter.FormatDelta(delta));

    [Fact]
    public void Exact_uses_digit_grouping()
    {
        Assert.Equal("1,234,567 bytes", SizeFormatter.FormatExact(1_234_567));
        Assert.Equal("1 byte", SizeFormatter.FormatExact(1));
    }
}
