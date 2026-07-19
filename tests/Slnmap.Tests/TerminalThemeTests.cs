using System.Globalization;
using Xunit;

namespace Slnmap.Tests;

/// <summary>
/// Covers the CLI styling helpers directly (there is no real TTY under test): the palette's colour and
/// no-colour paths, list wrapping with a hanging indent, and the friendly timestamp.
/// </summary>
public sealed class TerminalThemeTests
{
    private const char Esc = (char)0x1b;

    [Fact]
    public void Palette_NoColor_ReturnsInputUnchanged()
    {
        var p = Palette.CreateForTests(color: false);

        Assert.Equal("Nodes:", p.Label("Nodes:"));
        Assert.Equal("1107", p.Number("1107"));
        Assert.Equal("Method", p.Type("Method"));
        Assert.Equal("boom", p.Error("boom"));
    }

    [Fact]
    public void Palette_Color_WrapsWithAnsiCodesButPreservesText()
    {
        var p = Palette.CreateForTests(color: true);

        Assert.Equal($"{Esc}[97m1107{Esc}[0m", p.Number("1107"));
        Assert.Equal($"{Esc}[36mMethod{Esc}[0m", p.Type("Method"));
        Assert.Equal($"{Esc}[31mboom{Esc}[0m", p.Error("boom"));
        Assert.Equal($"{Esc}[32mok{Esc}[0m", p.Success("ok"));
        Assert.Equal($"{Esc}[33mwarn{Esc}[0m", p.Warn("warn"));

        // The visible content survives colouring (codes only wrap it).
        Assert.Contains("1107", p.Number("1107"), StringComparison.Ordinal);
    }

    [Fact]
    public void WrapList_WrapsToWidthWithHangingIndent()
    {
        string[] items = ["Alpha", "Bravo", "Charlie", "Delta", "Echo"];

        var lines = CliFormat.WrapList(items, indent: 2, width: 20);

        Assert.True(lines.Count > 1, "expected the list to wrap at width 20");
        Assert.All(lines, l => Assert.StartsWith("  ", l, StringComparison.Ordinal)); // hanging indent
        Assert.All(lines, l => Assert.True(l.Length <= 20, $"line exceeds width: '{l}'"));

        string joined = string.Join(" ", lines.Select(l => l.Trim()));
        Assert.Contains("Alpha,", joined, StringComparison.Ordinal); // comma-joined
        Assert.Contains("Echo", joined, StringComparison.Ordinal);   // last item, no trailing comma
        Assert.DoesNotContain("Echo,", joined, StringComparison.Ordinal);
    }

    [Fact]
    public void WrapList_Empty_ReturnsNoLines()
    {
        Assert.Empty(CliFormat.WrapList([], indent: 2, width: 80));
    }

    [Fact]
    public void FriendlyTimestamp_RecentInstant_ShowsRelativeAgo()
    {
        string threeHoursAgo = DateTimeOffset.UtcNow.AddHours(-3).ToString("O", CultureInfo.InvariantCulture);

        string result = CliFormat.FriendlyTimestamp(threeHoursAgo);

        Assert.Contains("UTC", result, StringComparison.Ordinal);
        Assert.Contains("3h ago", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FriendlyTimestamp_FormatsAbsoluteUtc()
    {
        string result = CliFormat.FriendlyTimestamp("2020-01-02T03:04:05.0000000+00:00");

        Assert.Contains("02 Jan 2020, 03:04 UTC", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FriendlyTimestamp_Unparseable_ReturnsRaw()
    {
        Assert.Equal("test", CliFormat.FriendlyTimestamp("test"));
    }
}
