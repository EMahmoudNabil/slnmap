using Slnmap.Analysis;
using Slnmap.Core.Graph;
using Slnmap.Mcp;
using Slnmap.Storage;
using Xunit;

namespace Slnmap.Tests;

/// <summary>
/// v0.14.0 (reports/v0140-gate8-verification.md §7): a project whose restore never ran or failed
/// still loads in MSBuildWorkspace — with no metadata references and no workspace diagnostic — so
/// it was analyzed silently with every framework type unresolved (blazor-workshop: 0 endpoints
/// before <c>dotnet restore</c>, 6 after). It is now disclosed per project, counted, warned about,
/// surfaced on every MCP answer, checked by <c>doctor</c>, and a change in restore state forces a
/// full re-analysis (no document changes on restore, so an incremental run would keep every
/// stale result).
/// </summary>
public sealed class ProjectNotRestoredTests : IDisposable
{
    private const string WebProject = """
        <Project Sdk="Microsoft.NET.Sdk.Web">
          <PropertyGroup>
            <TargetFramework>net9.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
            <Nullable>enable</Nullable>
          </PropertyGroup>
        </Project>
        """;

    private const string Controller = """
        using Microsoft.AspNetCore.Mvc;

        namespace Probe;

        [ApiController]
        [Route("api/probe")]
        public class ProbeController : ControllerBase
        {
            [HttpGet]
            public int Get() => 1;
        }
        """;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "slnmap-not-restored", Guid.NewGuid().ToString("N"));

    public ProjectNotRestoredTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }

    private string CreateProject(string name = "Probe")
    {
        string projectDirectory = Path.Combine(_directory, name);
        Directory.CreateDirectory(projectDirectory);
        string csproj = Path.Combine(projectDirectory, name + ".csproj");
        File.WriteAllText(csproj, WebProject);
        File.WriteAllText(Path.Combine(projectDirectory, "ProbeController.cs"), Controller);
        return csproj;
    }

    private static string AssetsPath(string csproj) =>
        Path.Combine(Path.GetDirectoryName(csproj)!, "obj", "project.assets.json");

    private static void WriteAssets(string csproj, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AssetsPath(csproj))!);
        File.WriteAllText(AssetsPath(csproj), json);
    }

    [Fact]
    public async Task Unrestored_IsDisclosedCountedAndWarned_ThenRestoreForcesAFullReanalysis()
    {
        string csproj = CreateProject();

        var warnings = new List<string>();
        var before = await new RoslynSolutionAnalyzer(warnings.Add).AnalyzeAsync(csproj);

        var disclosure = Assert.Single(before.Graph.Disclosures, d => d.Kind == DisclosureKinds.ProjectNotRestored);
        Assert.Equal(csproj, disclosure.FilePath);
        Assert.Equal("Probe", DisclosureKinds.ProjectNotRestoredName(disclosure.Detail));
        Assert.Contains("no references resolved", DisclosureKinds.ProjectNotRestoredReason(disclosure.Detail), StringComparison.Ordinal);
        Assert.Equal(1, before.Stats.ProjectsNotRestored);
        Assert.Contains(warnings, w => w.Contains("'Probe' was analyzed without its dependencies", StringComparison.Ordinal)
            && w.Contains("dotnet restore", StringComparison.Ordinal));
        // The silent failure being disclosed: without references the controller is not seen at all.
        Assert.DoesNotContain(before.Graph.Nodes, n => n.Kind == NodeKind.Endpoint);

        DotNet.Run($"restore \"{csproj}\"", _directory);

        // No document changed, so only the restore-state check can trigger a rebuild.
        warnings.Clear();
        var after = await new RoslynSolutionAnalyzer(warnings.Add).AnalyzeAsync(csproj, before);

        Assert.Contains(warnings, w => w.Contains("restore state changed", StringComparison.Ordinal));
        Assert.DoesNotContain(after.Graph.Disclosures, d => d.Kind == DisclosureKinds.ProjectNotRestored);
        Assert.Equal(0, after.Stats.ProjectsNotRestored);
        Assert.Equal(0, after.Stats.DocumentsSkipped); // full: every document re-walked (restore adds generated ones)
        Assert.Contains(after.Graph.Nodes, n => n.Kind == NodeKind.Endpoint);

        // Steady state: a further run is incremental again (nothing re-walked, nothing disclosed).
        warnings.Clear();
        var steady = await new RoslynSolutionAnalyzer(warnings.Add).AnalyzeAsync(csproj, after);
        Assert.DoesNotContain(warnings, w => w.Contains("restore state changed", StringComparison.Ordinal));
        Assert.Equal(0, steady.Stats.DocumentsAnalyzed);
        Assert.Contains(steady.Graph.Nodes, n => n.Kind == NodeKind.Endpoint);
    }

    [Fact]
    public async Task RestoreThatRecordedErrors_IsDisclosedEvenWithReferencesResolved()
    {
        string csproj = CreateProject();
        DotNet.Run($"restore \"{csproj}\"", _directory);

        // A real assets file, plus the error entry NuGet writes when a restore fails (e.g. the
        // NU1504 that breaks eShopOnContainers' restore on SDK 10).
        string assets = File.ReadAllText(AssetsPath(csproj)).TrimEnd();
        Assert.EndsWith("}", assets, StringComparison.Ordinal);
        File.WriteAllText(AssetsPath(csproj), assets[..^1]
            + """, "logs": [ { "code": "NU1504", "level": "Error", "message": "Duplicate 'PackageReference' items found." } ] }""");

        var snapshot = await new RoslynSolutionAnalyzer().AnalyzeAsync(csproj);

        var disclosure = Assert.Single(snapshot.Graph.Disclosures, d => d.Kind == DisclosureKinds.ProjectNotRestored);
        Assert.Contains("restore recorded errors (NU1504)", disclosure.Detail, StringComparison.Ordinal);
        Assert.Equal(1, snapshot.Stats.ProjectsNotRestored);
    }

    [Fact]
    public void AssetsErrors_ReadsOnlyErrorLevelEntries_AndNeverThrows()
    {
        string csproj = CreateProject();
        Assert.Null(ProjectRestoreCheck.AssetsErrors(csproj)); // no assets file

        WriteAssets(csproj, """{ "version": 3, "logs": [ { "code": "NU1504", "level": "Error" }, { "code": "NU1101", "level": "Error" }, { "code": "NU1504", "level": "Error" } ] }""");
        Assert.Equal("NU1101, NU1504", ProjectRestoreCheck.AssetsErrors(csproj));

        WriteAssets(csproj, """{ "version": 3, "logs": [ { "code": "NU1603", "level": "Warning" } ] }""");
        Assert.Null(ProjectRestoreCheck.AssetsErrors(csproj));

        WriteAssets(csproj, """{ "version": 3, "targets": {} }""");
        Assert.Null(ProjectRestoreCheck.AssetsErrors(csproj));

        WriteAssets(csproj, """{ "version": 3, "logs": [ { "level": "Error" } ] }""");
        Assert.Equal("unknown", ProjectRestoreCheck.AssetsErrors(csproj));

        WriteAssets(csproj, """{ "logs": [ not json""");
        Assert.Null(ProjectRestoreCheck.AssetsErrors(csproj));
    }

    [Fact]
    public void Doctor_FlagsUnrestoredAndFailedProjectsOfASolution_AndPassesOnceRestored()
    {
        string first = CreateProject("First");
        string second = CreateProject("Second");
        string sln = Path.Combine(_directory, "Probe.sln");
        File.WriteAllText(sln, """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "First", "First\First.csproj", "{11111111-1111-1111-1111-111111111111}"
            EndProject
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Second", "Second\Second.csproj", "{22222222-2222-2222-2222-222222222222}"
            EndProject
            """);

        var check = EnvironmentDoctor.CheckProjectsRestored(sln);
        Assert.False(check.Ok);
        Assert.Contains("2 of 2 project(s) in Probe.sln not restored", check.Detail, StringComparison.Ordinal);
        Assert.Contains("First (never restored)", check.Detail, StringComparison.Ordinal);
        Assert.Contains("dotnet restore", check.Fix, StringComparison.Ordinal);

        WriteAssets(first, """{ "version": 3 }""");
        WriteAssets(second, """{ "version": 3, "logs": [ { "code": "NU1101", "level": "Error" } ] }""");
        check = EnvironmentDoctor.CheckProjectsRestored(_directory); // a directory holding one .sln resolves to it
        Assert.False(check.Ok);
        Assert.Contains("1 of 2", check.Detail, StringComparison.Ordinal);
        Assert.Contains("Second (restore errors: NU1101)", check.Detail, StringComparison.Ordinal);

        WriteAssets(second, """{ "version": 3 }""");
        Assert.True(EnvironmentDoctor.CheckProjectsRestored(sln).Ok);
        Assert.True(EnvironmentDoctor.CheckProjectsRestored(first).Ok); // a single .csproj target

        // .slnx: the XML solution format.
        string slnx = Path.Combine(_directory, "Probe.slnx");
        File.WriteAllText(slnx, """<Solution><Project Path="First/First.csproj" /><Project Path="Third/Third.csproj" /></Solution>""");
        CreateProject("Third");
        check = EnvironmentDoctor.CheckProjectsRestored(slnx);
        Assert.False(check.Ok);
        Assert.Contains("Third (never restored)", check.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task McpNote_ListsTheProjects_CappedAndAbsentWhenAllRestored()
    {
        string db = Path.Combine(_directory, "graph.db");
        await using var store = new SqliteGraphStore(db);
        await store.InitializeAsync();

        var graph = new CodeGraph();
        graph.AddNode(SymbolNode.Create(NodeKind.Class, "C", "N.C", "c.cs"));
        await store.SaveAsync(graph, [], new Dictionary<string, string>(StringComparer.Ordinal));
        Assert.Null(await new SlnmapQueries(store).NotRestoredNoteAsync());

        for (int i = 1; i <= SlnmapQueries.NotRestoredNamesCap + 2; i++)
        {
            graph.AddDisclosure(new Disclosure(
                DisclosureKinds.ProjectNotRestored,
                DisclosureKinds.ProjectNotRestoredDetail($"P{i}", "no references resolved; restore never ran or failed"),
                $"P{i}.csproj",
                0));
        }

        await store.SaveAsync(graph, [], new Dictionary<string, string>(StringComparer.Ordinal));
        string? note = await new SlnmapQueries(store).NotRestoredNoteAsync();

        Assert.NotNull(note);
        Assert.StartsWith("Warning: incomplete graph. 7 project(s)", note, StringComparison.Ordinal);
        Assert.Contains("P1, P2, P3, P4, P5 (+2 more)", note, StringComparison.Ordinal);
        Assert.Contains("dotnet restore", note, StringComparison.Ordinal);
    }
}
