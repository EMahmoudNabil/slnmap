using Slnmap.Core.Graph;
using Slnmap.Mcp;
using Xunit;

namespace Slnmap.Tests;

/// <summary>
/// v0.6.1 LIE-2 fix: anonymous types produce no nodes at all. Structurally identical anonymous
/// types render identical FQNs ("&lt;anonymous type: int Id, string Label&gt;"), and FQN is node
/// identity — so before the fix, the ones declared in FixtureApp/AnonymousReport.cs and
/// FixtureCli/AnonymousReport.cs collapsed into ONE node pinned to whichever file was analyzed
/// first, fabricating a cross-project References edge between two projects that share no code
/// (the eval's 9 fake Application→Web/Infrastructure rows).
/// </summary>
public sealed class AnonymousTypeDedupTests : IClassFixture<AnalyzedFixtureSolution>
{
    private readonly AnalyzedFixtureSolution _fixture;

    public AnonymousTypeDedupTests(AnalyzedFixtureSolution fixture) => _fixture = fixture;

    private CodeGraph Graph => _fixture.Graph;

    [Fact]
    public void AnonymousTypes_AndTheirProperties_ProduceNoNodes()
    {
        Assert.DoesNotContain(Graph.Nodes, n => n.Fqn.Contains("<anonymous", StringComparison.Ordinal));
    }

    [Fact]
    public void NamedTupleElements_ProduceNoNodes()
    {
        // Same defect class discovered during the v0.6.1 self-benchmark: named tuple elements
        // are in-source IFieldSymbols whose FQNs render as "(int First, int Second).First" —
        // identical across files, uncontained (the tuple type itself is never a node), and
        // collapsing exactly like anonymous types. No real FQN starts with "(".
        Assert.DoesNotContain(Graph.Nodes, n => n.Fqn.StartsWith('('));
    }

    [Fact]
    public void StructurallyIdenticalAnonymousTypesAndTuples_CreateNoCrossProjectEdges()
    {
        // Each method's only non-local expressions are its anonymous type / named tuple and
        // their members — with those unmodeled, none may contribute any References edge, let
        // alone one crossing into the other project's files.
        foreach (var fqn in new[]
                 {
                     "Fixture.App.AppReport.Build()",
                     "Fixture.Cli.CliReport.Build()",
                     "Fixture.App.AppReport.Sum()",
                     "Fixture.Cli.CliReport.Sum()",
                 })
        {
            var method = GraphAssert.Node(Graph, NodeKind.Method, fqn);
            Assert.Empty(Graph.OutgoingEdges(method.Id, RelationshipKind.References));
        }
    }
}

/// <summary>LIE 2 at the tool surface: the overview's derived project-dependency rows.</summary>
public sealed class AnonymousTypeOverviewTests : IClassFixture<AnalyzedFixtureGraphStore>
{
    private readonly SlnmapQueries _queries;

    public AnonymousTypeOverviewTests(AnalyzedFixtureGraphStore fixture) => _queries = new SlnmapQueries(fixture.Store);

    [Fact]
    public async Task ArchitectureOverview_ShowsNoAnonymousTypeInducedDependencyRows()
    {
        // FixtureApp and FixtureCli share no real code; their only overlap is the structurally
        // identical anonymous type. Any dependency row between them would be fabricated.
        string result = await _queries.GetArchitectureOverviewAsync();

        Assert.DoesNotContain("FixtureApp -> FixtureCli", result, StringComparison.Ordinal);
        Assert.DoesNotContain("FixtureCli -> FixtureApp", result, StringComparison.Ordinal);
    }
}
