using Slnmap.Core.Analysis;
using Xunit;

namespace Slnmap.Tests;

/// <summary>
/// Issue #16: per-update progress writes flooded redirected/CI output (the "\r" rewrite trick
/// only works on a live terminal). Redirected stderr now gets milestone lines only — the first
/// report of each stage plus every 10% — while interactive terminals keep the single overwriting
/// line. Console.Error is process-global and other parallel tests may interleave writes, so every
/// assertion counts only lines carrying this test's unique stage marker.
/// </summary>
public sealed class ConsoleStatusLineTests
{
    [Fact]
    public void Redirected_ThousandsOfUpdates_CollapseToMilestones()
    {
        string stage = $"Analyzing-{Guid.NewGuid():N}";
        var lines = CaptureStderr(status =>
        {
            for (int i = 1; i <= 3484; i++)
            {
                status.Report(new AnalysisProgress(stage, i, 3484));
            }
        });

        var mine = lines.Where(l => l.Contains(stage, StringComparison.Ordinal)).ToList();
        Assert.InRange(mine.Count, 2, 12);                       // was 3,484 lines before the fix
        Assert.Contains(mine, l => l.Contains("3484/3484", StringComparison.Ordinal));
        Assert.All(mine, l => Assert.DoesNotContain('\r', l));
    }

    [Fact]
    public void Redirected_UnknownTotalStage_PrintsOnlyItsFirstReport()
    {
        string stage = $"Loading-{Guid.NewGuid():N}";
        var lines = CaptureStderr(status =>
        {
            for (int i = 0; i < 25; i++)
            {
                status.Report(new AnalysisProgress(stage, i, 0));
            }
        });

        Assert.Single(lines, l => l.Contains(stage, StringComparison.Ordinal));
    }

    [Fact]
    public void Redirected_StageChange_AlwaysPrints()
    {
        string a = $"StageA-{Guid.NewGuid():N}";
        string b = $"StageB-{Guid.NewGuid():N}";
        var lines = CaptureStderr(status =>
        {
            status.Report(new AnalysisProgress(a, 1, 0));
            status.Report(new AnalysisProgress(b, 1, 100));
        });

        Assert.Single(lines, l => l.Contains(a, StringComparison.Ordinal));
        Assert.Single(lines, l => l.Contains(b, StringComparison.Ordinal));
    }

    [Fact]
    public void Interactive_KeepsTheSingleOverwritingLine()
    {
        string stage = $"Analyzing-{Guid.NewGuid():N}";
        string raw = CaptureStderrRaw(redirected: false, status =>
        {
            status.Report(new AnalysisProgress(stage, 1, 10));
            status.Report(new AnalysisProgress(stage, 2, 10));
        });

        // Every report is a carriage-return rewrite, never a newline per update.
        Assert.Contains($"\r{stage} 1/10", raw, StringComparison.Ordinal);
        Assert.Contains($"\r{stage} 2/10", raw, StringComparison.Ordinal);
        Assert.DoesNotContain($"{stage} 1/10\n", raw.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void Redirected_WriteLine_HasNoPaddingJunk()
    {
        string marker = $"warning-{Guid.NewGuid():N}";
        var lines = CaptureStderr(status => status.WriteLine(marker));

        string mine = Assert.Single(lines, l => l.Contains(marker, StringComparison.Ordinal));
        Assert.Equal(marker, mine); // no '\r' + 70-space blanking prefix in a capture
    }

    private static List<string> CaptureStderr(Action<ConsoleStatusLine> act) =>
        CaptureStderrRaw(redirected: true, act)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))
            .ToList();

    private static string CaptureStderrRaw(bool redirected, Action<ConsoleStatusLine> act)
    {
        var writer = new StringWriter();
        var original = Console.Error;
        Console.SetError(writer);
        try
        {
            act(new ConsoleStatusLine(redirected));
        }
        finally
        {
            Console.SetError(original);
        }

        return writer.ToString();
    }
}
