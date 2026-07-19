using Slnmap.Core.Graph;
using Xunit;

namespace Slnmap.Tests;

public sealed class SourceSpanTests
{
    [Fact]
    public void Constructor_RejectsNegativeStart()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SourceSpan(-1, 5));
    }

    [Fact]
    public void Constructor_RejectsEndBeforeStart()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SourceSpan(10, 9));
    }

    [Fact]
    public void Length_IsEndMinusStart()
    {
        Assert.Equal(7, new SourceSpan(3, 10).Length);
        Assert.Equal(0, new SourceSpan(3, 3).Length);
    }

    [Fact]
    public void ToString_RoundTripsThroughParse()
    {
        var span = new SourceSpan(120, 480);

        Assert.Equal("120-480", span.ToString());
        Assert.Equal(span, SourceSpan.Parse(span.ToString()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12")]
    [InlineData("-5")]
    [InlineData("a-b")]
    [InlineData("10-2")]
    [InlineData("1-2-3")]
    public void TryParse_RejectsMalformedInput(string? input)
    {
        Assert.False(SourceSpan.TryParse(input, out _));
    }

    [Fact]
    public void Parse_ThrowsOnMalformedInput()
    {
        Assert.Throws<FormatException>(() => SourceSpan.Parse("nope"));
    }
}
