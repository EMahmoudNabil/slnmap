using System.Diagnostics;
using Xunit;

namespace Slnmap.Tests;

/// <summary>
/// Task A (slnmap-ts npm package — Part 1 of 2 for the ts-extractor feature,
/// reports/ts-extractor-implementation.md): verifies the REAL extractor's JSON output against
/// the investigation's golden fixture (tests/fixtures-ts/frontend-fixture/expected-callsites.json)
/// from the .NET test suite's own process-invocation convention. No database or ingestion is
/// involved here — that is Task B, tracked separately by
/// TsExtractorGapTests.Gap_AnalyzeTsVerb_ExtractsFixtureCallSitesIntoTheDatabase, which stays
/// failing until then. Requires `npm run build` to have been run in src/slnmap-ts/ first — this
/// file adds no build-orchestration and no production C#.
/// </summary>
public sealed class TsExtractorPackageTests
{
    [Fact]
    public void RealExtractor_AgainstInvestigationFixture_MatchesGoldenArtifactExactly()
    {
        string packageDir = Path.Combine(TestPaths.RepoRoot, "src", "slnmap-ts");
        string cliJs = Path.Combine(packageDir, "dist", "cli.js");
        Assert.True(File.Exists(cliJs), $"slnmap-ts not built at {cliJs} — run `npm run build` in {packageDir}.");

        string fixtureDir = Path.Combine(TestPaths.RepoRoot, "tests", "fixtures-ts", "frontend-fixture");
        string expectedPath = Path.Combine(fixtureDir, "expected-callsites.json");
        string outPath = Path.Combine(Path.GetTempPath(), $"slnmap-ts-artifact-{Guid.NewGuid():N}.json");

        var psi = new ProcessStartInfo("node")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(cliJs);
        psi.ArgumentList.Add("extract");
        psi.ArgumentList.Add(fixtureDir);
        psi.ArgumentList.Add("--tsconfig");
        psi.ArgumentList.Add(Path.Combine(fixtureDir, "tsconfig.json"));
        psi.ArgumentList.Add("--out");
        psi.ArgumentList.Add(outPath);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start node.");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        bool exited = process.WaitForExit(60_000);
        if (!exited)
        {
            process.Kill(entireProcessTree: true);
        }

        Assert.True(exited, "slnmap-ts extract timed out.");
        Assert.True(process.ExitCode == 0, $"slnmap-ts extract failed (exit {process.ExitCode}).\nstdout:\n{stdout}\nstderr:\n{stderr}");

        try
        {
            // Line-ending-agnostic: git may check the golden fixture out with CRLF, while node
            // writes LF explicitly — the determinism claim is about content, not EOL style.
            string actualText = File.ReadAllText(outPath).Replace("\r\n", "\n");
            string expectedText = File.ReadAllText(expectedPath).Replace("\r\n", "\n");
            Assert.Equal(expectedText, actualText);
        }
        finally
        {
            if (File.Exists(outPath))
            {
                File.Delete(outPath);
            }
        }
    }
}
