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

    /// <summary>
    /// What an unrestored project looks like depends on the SDK: under SDK 10 it has no references
    /// at all; under SDK 9 the framework still resolves and only NuGet packages are missing (the
    /// Linux CI leg, SDK 9 only, caught a check that knew only the first shape). Run under the
    /// default SDK and, when installed, pinned to SDK 9.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("9")]
    public async Task Unrestored_IsDisclosedCountedAndWarned_ThenRestoreForcesAFullReanalysis(string? sdkMajor)
    {
        if (sdkMajor is not null && !await PinSdkAsync(sdkMajor))
        {
            return; // that SDK is not installed here; the default-SDK case still runs
        }

        string csproj = CreateProject();

        var warnings = new List<string>();
        var before = await new RoslynSolutionAnalyzer(warnings.Add).AnalyzeAsync(csproj);

        var disclosure = Assert.Single(before.Graph.Disclosures, d => d.Kind == DisclosureKinds.ProjectNotRestored);
        Assert.Equal(csproj, disclosure.FilePath);
        Assert.Equal("Probe", DisclosureKinds.ProjectNotRestoredName(disclosure.Detail));
        string reason = DisclosureKinds.ProjectNotRestoredReason(disclosure.Detail);
        Assert.True(
            reason.Contains("no references resolved", StringComparison.Ordinal)
                || reason.Contains("no project.assets.json", StringComparison.Ordinal),
            reason);
        Assert.Equal(1, before.Stats.ProjectsNotRestored);
        Assert.Contains(warnings, w => w.Contains("'Probe' was analyzed with missing dependencies", StringComparison.Ordinal)
            && w.Contains("dotnet restore", StringComparison.Ordinal));

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
    public async Task RestoredWithArtifactsOutputLayout_IsNotDisclosed()
    {
        // UseArtifactsOutput moves restore output to artifacts/obj/<project>/: a restored project
        // there must not be reported as unrestored.
        File.WriteAllText(Path.Combine(_directory, "Directory.Build.props"), """
            <Project>
              <PropertyGroup>
                <UseArtifactsOutput>true</UseArtifactsOutput>
              </PropertyGroup>
            </Project>
            """);
        string csproj = CreateProject();
        DotNet.Run($"restore \"{csproj}\"", _directory);
        Assert.False(File.Exists(AssetsPath(csproj)), "expected the artifacts layout, not obj/");
        Assert.True(File.Exists(Path.Combine(_directory, "artifacts", "obj", "Probe", "project.assets.json")));

        var snapshot = await new RoslynSolutionAnalyzer().AnalyzeAsync(csproj);

        Assert.DoesNotContain(snapshot.Graph.Disclosures, d => d.Kind == DisclosureKinds.ProjectNotRestored);
        Assert.Equal(0, snapshot.Stats.ProjectsNotRestored);
        Assert.True(EnvironmentDoctor.CheckProjectsRestored(csproj).Ok);
    }

    [Fact]
    public void Doctor_IgnoresALegacyProjectWithoutAssets()
    {
        // A non-SDK project (packages.config era) never gets project.assets.json.
        string directory = Path.Combine(_directory, "Legacy");
        Directory.CreateDirectory(directory);
        string csproj = Path.Combine(directory, "Legacy.csproj");
        File.WriteAllText(csproj, """
            <Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <Import Project="$(MSBuildToolsPath)\Microsoft.CSharp.targets" />
            </Project>
            """);

        Assert.True(EnvironmentDoctor.CheckProjectsRestored(csproj).Ok);
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

    /// <summary>Pins <see cref="_directory"/> to the newest installed SDK of <paramref name="major"/>; false when none is installed.</summary>
    private async Task<bool> PinSdkAsync(string major)
    {
        var sdks = await DotnetSdks.ListAsync();
        if (!sdks.Versions.Any(v => v.StartsWith(major + ".", StringComparison.Ordinal)))
        {
            return false;
        }

        File.WriteAllText(Path.Combine(_directory, "global.json"),
            $$"""{ "sdk": { "version": "{{major}}.0.100", "rollForward": "latestMinor" } }""");
        return true;
    }

    [Fact]
    public async Task StaleRestore_PackageAddedAfterRestore_IsDisclosed_AndClearsOnRestore()
    {
        // v0.14.1 (R1): the project was restored, then a package was added to the project file.
        string csproj = CreateProject();
        DotNet.Run($"restore \"{csproj}\"", _directory);
        File.WriteAllText(csproj, WebProject.Replace(
            "</Project>",
            """
              <ItemGroup>
                <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
              </ItemGroup>
            </Project>
            """,
            StringComparison.Ordinal));

        var stale = await new RoslynSolutionAnalyzer().AnalyzeAsync(csproj);
        var disclosure = Assert.Single(stale.Graph.Disclosures, d => d.Kind == DisclosureKinds.ProjectNotRestored);
        Assert.Contains("restore is out of date: Newtonsoft.Json not restored yet", disclosure.Detail, StringComparison.Ordinal);
        var check = EnvironmentDoctor.CheckProjectsRestored(csproj);
        Assert.False(check.Ok);
        Assert.Contains("restore out of date: Newtonsoft.Json", check.Detail, StringComparison.Ordinal);

        DotNet.Run($"restore \"{csproj}\" --force", _directory);
        var fresh = await new RoslynSolutionAnalyzer().AnalyzeAsync(csproj, stale);
        Assert.DoesNotContain(fresh.Graph.Disclosures, d => d.Kind == DisclosureKinds.ProjectNotRestored);
        Assert.True(EnvironmentDoctor.CheckProjectsRestored(csproj).Ok);
    }

    [Fact]
    public void MissingPackages_CountsOnlyUnconditionalLiteralReferences()
    {
        string csproj = Path.Combine(_directory, "Declared.csproj");
        File.WriteAllText(csproj, """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Restored.Package" Version="1.0.0" />
                <PackageReference Include="missing.package; Other.Missing" Version="1.0.0" />
                <PackageReference Include="$(FromAProperty)" Version="1.0.0" />
                <PackageReference Update="Updated.Only" Version="2.0.0" />
                <PackageReference Include="Conditional.Item" Version="1.0.0" Condition="'$(X)' == 'y'" />
                <PackageReference Include="Microsoft.AspNetCore.App" />
              </ItemGroup>
              <Choose>
                <When Condition="'$(X)' == 'y'"><ItemGroup><PackageReference Include="In.When" Version="1.0.0" /></ItemGroup></When>
                <Otherwise><ItemGroup><PackageReference Include="In.Otherwise" Version="1.0.0" /></ItemGroup></Otherwise>
              </Choose>
              <Target Name="Late"><ItemGroup><PackageReference Include="In.Target" Version="1.0.0" /></ItemGroup></Target>
              <ItemGroup Condition="'$(TargetFramework)' == 'net48'">
                <PackageReference Include="Conditional.Group" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """);
        string assets = Path.Combine(_directory, "project.assets.json");
        File.WriteAllText(assets, """
            { "version": 3, "targets": { "net9.0": {} }, "libraries": {},
              "project": { "frameworks": { "net9.0": { "dependencies": { "restored.package": { "target": "Package" } } } } } }
            """);

        Assert.Equal(["missing.package", "Other.Missing"], ProjectRestoreCheck.MissingPackages(csproj, assets));

        File.WriteAllText(assets, """{ "logs": [ not json""");
        Assert.Empty(ProjectRestoreCheck.MissingPackages(csproj, assets)); // unreadable: never a guess
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
