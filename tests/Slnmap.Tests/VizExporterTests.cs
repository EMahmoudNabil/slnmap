using System.Text.Json;
using Slnmap.Core.Graph;
using Slnmap.Core.Storage;
using Xunit;

namespace Slnmap.Tests;

/// <summary>
/// slnmap viz export against a hand-built graph: the embedded JSON must carry a correct
/// per-project hierarchy (namespace instances, single-child chain collapsing, orphan
/// reattachment), an edge census identical to the store's non-Contains census, real
/// file:line resolution, and escaping that survives hostile symbol names.
/// </summary>
public sealed class VizExporterTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "slnmap-viz-tests", Guid.NewGuid().ToString("N"));

    public VizExporterTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; the OS temp cleaner will get it eventually.
        }
    }

    private string P(params string[] parts) => Path.Combine([_directory, .. parts]);

    /// <summary>
    /// Two projects declaring types in the same namespace chain N → N.Deep, one dependency edge of
    /// each drawn kind between them, one orphan with a hostile FQN, and one real source file so a
    /// line number can actually resolve.
    /// </summary>
    private (CodeGraph Graph, Dictionary<string, string> Meta) BuildFixture()
    {
        Directory.CreateDirectory(P("src", "App"));
        File.WriteAllText(P("src", "App", "A.cs"), "// one\nclass A { }\n");

        var graph = new CodeGraph();
        var app = SymbolNode.Create(NodeKind.Project, "App", "App", P("src", "App", "App.csproj"));
        var lib = SymbolNode.Create(NodeKind.Project, "Lib", "Lib", P("src", "Lib", "Lib.csproj"));
        var nsN = SymbolNode.Create(NodeKind.Namespace, "N", "N");
        var nsDeep = SymbolNode.Create(NodeKind.Namespace, "Deep", "N.Deep");
        var typeA = SymbolNode.Create(NodeKind.Class, "A", "N.Deep.A", P("src", "App", "A.cs"), new SourceSpan(8, 15));
        var methodM = SymbolNode.Create(NodeKind.Method, "M", "N.Deep.A.M()", P("src", "App", "A.cs"), new SourceSpan(8, 15));
        var typeB = SymbolNode.Create(NodeKind.Class, "B", "N.Deep.B", P("src", "Lib", "B.cs"), new SourceSpan(0, 5));
        var methodF = SymbolNode.Create(NodeKind.Method, "F", "N.Deep.B.F()", P("src", "Lib", "B.cs"), new SourceSpan(0, 5));
        var evil = SymbolNode.Create(
            NodeKind.Class, "Evil", "<anonymous type: IRepository<> & 'x' </script>", P("src", "App", "Evil.cs"), new SourceSpan(0, 1));

        foreach (var node in new[] { app, lib, nsN, nsDeep, typeA, methodM, typeB, methodF, evil })
        {
            graph.AddNode(node);
        }

        // Contains is a DAG: BOTH projects contain the same namespace chain.
        graph.AddEdge(new RelationshipEdge(app.Id, nsN.Id, RelationshipKind.Contains));
        graph.AddEdge(new RelationshipEdge(lib.Id, nsN.Id, RelationshipKind.Contains));
        graph.AddEdge(new RelationshipEdge(nsN.Id, nsDeep.Id, RelationshipKind.Contains));
        graph.AddEdge(new RelationshipEdge(nsDeep.Id, typeA.Id, RelationshipKind.Contains));
        graph.AddEdge(new RelationshipEdge(nsDeep.Id, typeB.Id, RelationshipKind.Contains));
        graph.AddEdge(new RelationshipEdge(typeA.Id, methodM.Id, RelationshipKind.Contains));
        graph.AddEdge(new RelationshipEdge(typeB.Id, methodF.Id, RelationshipKind.Contains));
        // evil has NO Contains edge: it must reattach to App by file path.

        graph.AddEdge(new RelationshipEdge(methodM.Id, methodF.Id, RelationshipKind.Calls));
        graph.AddEdge(new RelationshipEdge(typeA.Id, typeB.Id, RelationshipKind.References));
        graph.AddEdge(new RelationshipEdge(typeA.Id, typeB.Id, RelationshipKind.Inherits));
        graph.AddEdge(new RelationshipEdge(typeA.Id, typeB.Id, RelationshipKind.Implements));

        var meta = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [MetaKeys.SolutionPath] = P("Fixture.sln"),
            [MetaKeys.LastAnalyzed] = "2026-07-22T00:00:00.0000000+00:00",
        };
        return (graph, meta);
    }

    private string Export(string? projectFilter = null)
    {
        var (graph, meta) = BuildFixture();
        string output = P($"graph-{Guid.NewGuid():N}.html");
        VizExporter.WriteHtml(graph, meta, output, projectFilter);
        return File.ReadAllText(output);
    }

    private static JsonDocument ExtractData(string html)
    {
        const string marker = "const DATA = ";
        int start = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "embedded DATA marker not found");
        start += marker.Length;

        // The template resource's line endings depend on the checkout (git core.autocrlf on
        // Windows rewrites LF -> CRLF), so accept either rather than assuming one.
        int end = html.IndexOf(";\r\n", start, StringComparison.Ordinal);
        if (end < 0)
        {
            end = html.IndexOf(";\n", start, StringComparison.Ordinal);
        }

        Assert.True(end >= 0, "embedded DATA statement terminator not found");
        return JsonDocument.Parse(html[start..end]);
    }

    private static (string Kind, string Name, string Fqn, string? File, int? Line, int Parent) Node(JsonDocument doc, JsonElement element)
    {
        var kinds = doc.RootElement.GetProperty("kinds");
        return (
            kinds[element[0].GetInt32()].GetString()!,
            element[1].GetString()!,
            element[2].GetString()!,
            element[3].ValueKind == JsonValueKind.Null ? null : element[3].GetString(),
            element[4].ValueKind == JsonValueKind.Null ? null : element[4].GetInt32(),
            element[5].GetInt32());
    }

    [Fact]
    public void Hierarchy_MaterializesNamespaceInstancesPerProject_AndCollapsesChains()
    {
        var doc = ExtractData(Export());
        var nodes = doc.RootElement.GetProperty("nodes").EnumerateArray().Select(e => Node(doc, e)).ToList();

        // Roots are exactly the two projects — never a raw namespace.
        var roots = nodes.Where(n => n.Parent == -1).ToList();
        Assert.Equal(2, roots.Count);
        Assert.All(roots, r => Assert.Equal("Project", r.Kind));

        // The shared N → N.Deep chain became ONE collapsed instance per project,
        // labeled with the full dotted path.
        var namespaces = nodes.Where(n => n.Kind == "Namespace").ToList();
        Assert.Equal(2, namespaces.Count);
        Assert.All(namespaces, ns => Assert.Equal("N.Deep", ns.Name));
        Assert.All(namespaces, ns => Assert.Equal("N.Deep", ns.Fqn));
        var namespaceParents = namespaces.Select(ns => nodes[ns.Parent].Name).OrderBy(n => n).ToArray();
        Assert.Equal(["App", "Lib"], namespaceParents);

        // Each type hangs off its OWN project's instance (resolved by file path).
        var typeA = nodes.Single(n => n.Fqn == "N.Deep.A");
        var typeB = nodes.Single(n => n.Fqn == "N.Deep.B");
        Assert.Equal("App", nodes[nodes[typeA.Parent].Parent].Name);
        Assert.Equal("Lib", nodes[nodes[typeB.Parent].Parent].Name);

        // Members hang off their types.
        var methodM = nodes.Single(n => n.Fqn == "N.Deep.A.M()");
        Assert.Equal("N.Deep.A", nodes[methodM.Parent].Fqn);
    }

    [Fact]
    public void Orphans_ReattachToProjectByFilePath()
    {
        var doc = ExtractData(Export());
        var nodes = doc.RootElement.GetProperty("nodes").EnumerateArray().Select(e => Node(doc, e)).ToList();

        var evil = nodes.Single(n => n.Name == "Evil");
        Assert.Equal("App", nodes[evil.Parent].Name); // straight under the project: no Contains data
        Assert.DoesNotContain(nodes, n => n.Name == "(unattributed)");
    }

    [Fact]
    public void EdgeCensus_MatchesTheStoredGraphMinusContains()
    {
        var (graph, meta) = BuildFixture();
        string output = P("census.html");
        VizExporter.WriteHtml(graph, meta, output, null);
        var doc = ExtractData(File.ReadAllText(output));

        var expected = graph.Edges
            .Where(e => e.Kind != RelationshipKind.Contains)
            .GroupBy(e => e.Kind.ToString())
            .ToDictionary(g => g.Key, g => g.Count());

        var edgeKinds = doc.RootElement.GetProperty("edgeKinds").EnumerateArray().Select(e => e.GetString()!).ToArray();
        var actual = doc.RootElement.GetProperty("edges").EnumerateArray()
            .GroupBy(e => edgeKinds[e[2].GetInt32()])
            .ToDictionary(g => g.Key, g => g.Count());

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ProjectLevelAggregation_ComputesFromEmbeddedData()
    {
        var doc = ExtractData(Export());
        var nodes = doc.RootElement.GetProperty("nodes").EnumerateArray().Select(e => Node(doc, e)).ToList();
        var edgeKinds = doc.RootElement.GetProperty("edgeKinds").EnumerateArray().Select(e => e.GetString()!).ToArray();

        string RootName(int i)
        {
            while (nodes[i].Parent >= 0)
            {
                i = nodes[i].Parent;
            }

            return nodes[i].Name;
        }

        var aggregated = doc.RootElement.GetProperty("edges").EnumerateArray()
            .Select(e => (From: RootName(e[0].GetInt32()), To: RootName(e[1].GetInt32()), Kind: edgeKinds[e[2].GetInt32()]))
            .GroupBy(x => x)
            .ToDictionary(g => g.Key, g => g.Count());

        // All four fixture edges run App → Lib, one per kind.
        Assert.Equal(4, aggregated.Count);
        Assert.All(aggregated, kv => Assert.Equal(("App", "Lib"), (kv.Key.From, kv.Key.To)));
        Assert.All(aggregated, kv => Assert.Equal(1, kv.Value));
    }

    [Fact]
    public void HostileNames_AreEscaped_AndSurviveRoundTrip()
    {
        string html = Export();

        // The data can never terminate the script block: the only two </script> in the whole file
        // are the template's own (library + app code), even though a node FQN contains one.
        Assert.Equal(2, html.Split("</script>").Length - 1);

        var doc = ExtractData(html);
        var nodes = doc.RootElement.GetProperty("nodes").EnumerateArray().Select(e => Node(doc, e)).ToList();
        Assert.Contains(nodes, n => n.Fqn == "<anonymous type: IRepository<> & 'x' </script>");
    }

    [Fact]
    public void FileAndLine_ResolveFromRealSource_AndDegradeToNull()
    {
        var doc = ExtractData(Export());
        var nodes = doc.RootElement.GetProperty("nodes").EnumerateArray().Select(e => Node(doc, e)).ToList();

        // A.cs exists: span offset 8 sits on line 2; paths are solution-relative with forward slashes.
        var typeA = nodes.Single(n => n.Fqn == "N.Deep.A");
        Assert.Equal("src/App/A.cs", typeA.File);
        Assert.Equal(2, typeA.Line);

        // B.cs was never written: the path survives, the line degrades to null.
        var typeB = nodes.Single(n => n.Fqn == "N.Deep.B");
        Assert.Equal("src/Lib/B.cs", typeB.File);
        Assert.Null(typeB.Line);

        // Projects keep a file, namespaces have none.
        Assert.Null(nodes.First(n => n.Kind == "Namespace").File);
    }

    [Fact]
    public void ProjectFilter_KeepsSubtreeAndStubs_OrRejectsUnknownName()
    {
        var doc = ExtractData(Export("App"));
        var nodes = doc.RootElement.GetProperty("nodes").EnumerateArray().Select(e => Node(doc, e)).ToList();

        // App's subtree (namespace instance, A, M, Evil) + both project nodes as stubs.
        Assert.Contains(nodes, n => n.Fqn == "N.Deep.A");
        Assert.Contains(nodes, n => n.Name == "Evil");
        Assert.DoesNotContain(nodes, n => n.Fqn == "N.Deep.B");
        Assert.Contains(nodes, n => n.Kind == "Project" && n.Name == "Lib");

        var (graph, meta) = BuildFixture();
        var error = Assert.Throws<ArgumentException>(
            () => VizExporter.WriteHtml(graph, meta, P("never.html"), "Nope"));
        Assert.Equal("Unknown project 'Nope'. Valid projects: App, Lib.", error.Message);
        Assert.False(File.Exists(P("never.html")));
    }
}
