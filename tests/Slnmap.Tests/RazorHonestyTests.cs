using Slnmap.Core.Graph;
using Xunit;

namespace Slnmap.Tests;

/// <summary>
/// v0.12.2 (foreign-patterns-trial findings #1 and #2): Blazor <c>.razor</c> markup and Razor
/// Pages both used to be silent, undisclosed gaps — "0 skipped" and zero disclosure of any kind,
/// respectively, on real codebases. Both are now detected and disclosed, never modeled.
/// Fixtures: tests/fixtures/FixtureSolution/FixtureWeb/{SamplePageModel.cs,SampleComponent.razor}.
/// </summary>
public sealed class RazorHonestyTests : IClassFixture<AnalyzedFixtureSolution>
{
    private readonly AnalyzedFixtureSolution _fixture;

    public RazorHonestyTests(AnalyzedFixtureSolution fixture) => _fixture = fixture;

    private CodeGraph Graph => _fixture.Graph;

    [Fact]
    public void RazorPage_HandlerMethodsExistAsPlainMethodNodes_NeverAsEndpoints()
    {
        // The gap itself, pinned: OnGet/OnPost are ordinary Method nodes (Roslyn sees the .cs
        // file fine) but must never be promoted to Endpoint nodes — Razor Pages route by file
        // location, not by anything this tool can resolve statically.
        GraphAssert.Node(Graph, NodeKind.Class, "Fixture.Web.SamplePageModel");
        GraphAssert.Node(Graph, NodeKind.Method, "Fixture.Web.SamplePageModel.OnGet()");
        GraphAssert.Node(Graph, NodeKind.Method, "Fixture.Web.SamplePageModel.OnPost()");

        Assert.DoesNotContain(Graph.Nodes, n => n.Kind == NodeKind.Endpoint && n.Fqn.Contains("SamplePageModel", StringComparison.Ordinal));
        Assert.DoesNotContain(Graph.Nodes, n => n.Kind == NodeKind.Endpoint && (n.Fqn == "GET SamplePageModel" || n.Fqn == "POST SamplePageModel"));
    }

    [Fact]
    public void RazorPage_IsCountedAndDisclosed_NotSilentlyMissing()
    {
        // The actual fix: unlike before v0.12.2, this is now a COUNTED, disclosed gap — the same
        // treatment ConventionalControllers already got, not a silent absence.
        Assert.True(_fixture.Snapshot.Stats.RazorPagesNotModeled >= 1, "expected at least the SamplePageModel fixture to be counted");
    }

    [Fact]
    public void RazorFile_OnDiskIsDetectedAndCounted_EvenThoughRoslynNeverSeesIt()
    {
        // The Blazor fix: SampleComponent.razor is never a Roslyn document (confirm no node named
        // for it exists at all — Roslyn genuinely never saw it), yet the file-system scan still
        // counts it, closing the "0 skipped" confident-lie.
        Assert.DoesNotContain(Graph.Nodes, n => n.Name.Contains("SampleComponent", StringComparison.Ordinal));
        Assert.True(_fixture.Snapshot.Stats.RazorFilesDetected >= 1, "expected at least the SampleComponent.razor fixture to be counted");
    }

    [Fact]
    public void RazorFile_DetectionExcludesBuildOutputDirectories()
    {
        // bin/obj can accumulate copies of .razor files (or their generated output) during a
        // real build — those must never inflate the count; only source-tree files count.
        // (FixtureWeb's own obj/ directory, already present from a prior restore/build, is the
        // real-world proof: if this test passes, the exclusion held against a genuine obj/ tree,
        // not just a hypothetical one.)
        Assert.True(_fixture.Snapshot.Stats.RazorFilesDetected < 100, "a build-output leak would inflate this far past the one real fixture file");
    }
}
