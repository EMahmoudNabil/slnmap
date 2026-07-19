using Slnmap.Analysis;
using Xunit;

namespace Slnmap.Tests;

public sealed class WarningReportTests
{
    // The exact shape MSBuildWorkspace surfaces a NuGet audit warning as (captured from a real run):
    // the "Msbuild failed when processing …" wrapper around the raw advisory message.
    private static string Audit(string project, string package, string version, string severity, string url) =>
        $"Msbuild failed when processing the file 'C:\\repo\\{project}\\{project}.csproj' with message: " +
        $"Package '{package}' {version} has a known {severity} severity vulnerability, {url}";

    [Fact]
    public void CountIsEveryWarning_UniqueCollapsesAcrossProjects()
    {
        var report = new WarningReport();
        // Same advisory reported against two projects, plus a second advisory for the same package.
        report.Add(Audit("Api", "Azure.Identity", "1.10.4", "moderate", "https://advisories/GHSA-aaaa"));
        report.Add(Audit("Web", "Azure.Identity", "1.10.4", "moderate", "https://advisories/GHSA-aaaa"));
        report.Add(Audit("Api", "Azure.Identity", "1.10.4", "high", "https://advisories/GHSA-bbbb"));

        Assert.Equal(3, report.Count);       // machine count: every raw warning
        Assert.Equal(2, report.UniqueCount); // two distinct advisories; the per-project repeat collapses
        Assert.True(report.HasWarnings);
    }

    [Fact]
    public void EmptyReport_HasNoWarnings()
    {
        var report = new WarningReport();

        Assert.False(report.HasWarnings);
        Assert.Equal(0, report.Count);
        Assert.Equal(0, report.UniqueCount);
        Assert.Empty(report.RenderVerbose());
        Assert.Equal("Warnings: 0 (0 unique) — run with --verbose for details.", report.SummaryLine());
    }

    [Fact]
    public void SummaryLine_IncludesOrOmitsVerboseHint()
    {
        var report = new WarningReport();
        report.Add(Audit("Api", "Azure.Identity", "1.10.4", "moderate", "https://advisories/GHSA-aaaa"));
        report.Add(Audit("Api", "Azure.Identity", "1.10.4", "high", "https://advisories/GHSA-bbbb"));

        Assert.Equal("Warnings: 2 (2 unique) — run with --verbose for details.", report.SummaryLine());
        Assert.Equal("Warnings: 2 (2 unique).", report.SummaryLine(includeVerboseHint: false));
    }

    [Fact]
    public void RenderVerbose_GroupsAuditWarningsByPackage()
    {
        var report = new WarningReport();
        report.Add(Audit("Api", "Azure.Identity", "1.10.4", "moderate", "https://advisories/GHSA-aaaa"));
        report.Add(Audit("Web", "Azure.Identity", "1.10.4", "high", "https://advisories/GHSA-bbbb"));

        var lines = report.RenderVerbose();

        // One group line for the package (severities highest-first, URLs and projects listed once).
        Assert.Equal(
            "workspace warning: Package Azure.Identity 1.10.4 — 2 known vulnerabilities (high, moderate): " +
            "https://advisories/GHSA-aaaa, https://advisories/GHSA-bbbb",
            lines[0]);
        Assert.Equal("  affected projects: Api, Web", lines[1]);
        Assert.Equal(2, lines.Count);
    }

    [Fact]
    public void RenderVerbose_SingleAdvisoryUsesSingularNoun()
    {
        var report = new WarningReport();
        report.Add(Audit("Api", "Azure.Identity", "1.10.4", "moderate", "https://advisories/GHSA-aaaa"));
        report.Add(Audit("Web", "Azure.Identity", "1.10.4", "moderate", "https://advisories/GHSA-aaaa"));

        var lines = report.RenderVerbose();

        Assert.Equal(
            "workspace warning: Package Azure.Identity 1.10.4 — 1 known vulnerability (moderate): " +
            "https://advisories/GHSA-aaaa",
            lines[0]);
        Assert.Equal("  affected projects: Api, Web", lines[1]);
    }

    [Fact]
    public void RenderVerbose_ListsNonAuditDiagnosticAsIs_WithWrapperStripped()
    {
        var report = new WarningReport();
        report.Add("Msbuild failed when processing the file 'C:\\repo\\Api\\Api.csproj' with message: " +
                   "warning MSB3277: conflicting versions of System.Text.Json");

        var lines = report.RenderVerbose();

        Assert.Equal("workspace warning: warning MSB3277: conflicting versions of System.Text.Json", lines[0]);
        Assert.Equal("  affected projects: Api", lines[1]);
    }

    [Fact]
    public void RenderVerbose_UnwrappedMessage_HasNoProjectLine()
    {
        var report = new WarningReport();
        report.Add("Skipping project 'Legacy': no compilation available.");

        var lines = report.RenderVerbose();

        Assert.Single(lines);
        Assert.Equal("workspace warning: Skipping project 'Legacy': no compilation available.", lines[0]);
    }

    [Fact]
    public void RenderVerbose_OrdersAuditGroupsBeforeOtherDiagnostics()
    {
        var report = new WarningReport();
        report.Add("Skipping project 'Legacy': no compilation available.");
        report.Add(Audit("Api", "Azure.Identity", "1.10.4", "moderate", "https://advisories/GHSA-aaaa"));

        var lines = report.RenderVerbose();

        Assert.StartsWith("workspace warning: Package Azure.Identity", lines[0]);
        Assert.Contains(lines, l => l.Contains("Skipping project 'Legacy'", StringComparison.Ordinal));
        Assert.True(
            lines.ToList().FindIndex(l => l.Contains("Package Azure.Identity")) <
            lines.ToList().FindIndex(l => l.Contains("Skipping project")));
    }

    [Theory]
    // MSBuild embeds the project path verbatim; the parser must find the project name regardless of
    // which separator the path uses or which OS is parsing it (Windows CI passed, Linux CI did not).
    [InlineData("C:\\repo\\Api\\Api.csproj", "Api")]          // Windows separators
    [InlineData("/home/runner/repo/Api/Api.csproj", "Api")]   // Unix separators
    [InlineData("/home/runner/repo\\Api\\Api.csproj", "Api")] // mixed separators
    public void RenderVerbose_ExtractsProjectName_AcrossSeparators(string projectPath, string expected)
    {
        var report = new WarningReport();
        report.Add(
            $"Msbuild failed when processing the file '{projectPath}' with message: " +
            "Package 'Azure.Identity' 1.10.4 has a known moderate severity vulnerability, https://advisories/GHSA-aaaa");

        Assert.Contains($"  affected projects: {expected}", report.RenderVerbose());
    }

    [Fact]
    public void RenderVerbose_ParsesAuditWarningWithoutUrl()
    {
        var report = new WarningReport();
        // The audit regex makes the URL optional; a body with no trailing URL must still parse and group.
        report.Add("Msbuild failed when processing the file 'C:\\repo\\Api\\Api.csproj' with message: " +
                   "Package 'Contoso.Pkg' 2.0.0 has a known high severity vulnerability");

        var lines = report.RenderVerbose();

        Assert.Equal("workspace warning: Package Contoso.Pkg 2.0.0 — 1 known vulnerability (high)", lines[0]);
        Assert.Equal("  affected projects: Api", lines[1]);
        Assert.Equal(1, report.UniqueCount);
    }

    [Fact]
    public void RenderVerbose_DifferentVersionsOfSamePackageAreSeparateGroups()
    {
        var report = new WarningReport();
        report.Add(Audit("Api", "Newtonsoft.Json", "12.0.1", "high", "https://advisories/GHSA-aaaa"));
        report.Add(Audit("Api", "Newtonsoft.Json", "13.0.0", "low", "https://advisories/GHSA-bbbb"));

        var groupLines = report.RenderVerbose().Where(l => l.StartsWith("workspace warning:", StringComparison.Ordinal)).ToList();

        Assert.Equal(2, groupLines.Count);
        Assert.Contains(groupLines, l => l.Contains("Newtonsoft.Json 12.0.1", StringComparison.Ordinal));
        Assert.Contains(groupLines, l => l.Contains("Newtonsoft.Json 13.0.0", StringComparison.Ordinal));
    }

    [Fact]
    public void RenderVerbose_OrdersAllSeveritiesHighestFirst()
    {
        var report = new WarningReport();
        report.Add(Audit("Api", "Pkg", "1.0.0", "low", "https://advisories/GHSA-1"));
        report.Add(Audit("Api", "Pkg", "1.0.0", "critical", "https://advisories/GHSA-2"));
        report.Add(Audit("Api", "Pkg", "1.0.0", "moderate", "https://advisories/GHSA-3"));
        report.Add(Audit("Api", "Pkg", "1.0.0", "high", "https://advisories/GHSA-4"));

        var line = report.RenderVerbose()[0];

        Assert.Contains("4 known vulnerabilities (critical, high, moderate, low)", line);
    }

    [Theory]
    [InlineData("Cannot open project 'C:\\r\\docker-compose.dcproj' because the file extension '.dcproj' is not associated with a language.", true)]
    [InlineData("Cannot open project 'C:\\r\\legacy.vcxproj' because the file extension '.vcxproj' is not associated with a language.", true)]
    [InlineData("Msbuild failed when processing the file 'C:\\r\\App.csproj' with message: Package 'X' 1.0.0 has a known low severity vulnerability, https://a/b", false)]
    [InlineData("Skipping project 'Legacy': no compilation available.", false)]
    public void IsNonLanguageProjectDiagnostic_MatchesOnlyLanguageless(string message, bool expected)
    {
        Assert.Equal(expected, WarningReport.IsNonLanguageProjectDiagnostic(message));
    }

    [Fact]
    public void Add_IsThreadSafe()
    {
        var report = new WarningReport();

        Parallel.For(0, 200, i =>
            report.Add(Audit("Api", "Pkg", "1.0.0", "low", $"https://advisories/GHSA-{i:D4}")));

        Assert.Equal(200, report.Count);
        Assert.Equal(200, report.UniqueCount);
    }
}
