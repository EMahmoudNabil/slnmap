using Slnmap.Core.Graph;
using Slnmap.Core.Storage;
using Slnmap.Mcp;
using Slnmap.Storage;

namespace Slnmap.Tests;

/// <summary>
/// A synthetic graph persisted to a throwaway SQLite database, so the v0.3.0 query tools can be tested
/// deterministically without a full Roslyn analysis. Dispose to delete the temp database.
/// </summary>
internal sealed class TestGraph : IAsyncDisposable
{
    private readonly string _directory;

    private TestGraph(string directory, SqliteGraphStore store)
    {
        _directory = directory;
        Store = store;
    }

    public SqliteGraphStore Store { get; }

    public SlnmapQueries Queries => new(Store);

    public static async Task<TestGraph> CreateAsync(CodeGraph graph)
    {
        string directory = Path.Combine(Path.GetTempPath(), "slnmap-synth", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var store = new SqliteGraphStore(Path.Combine(directory, "g.db"));
        var meta = new Dictionary<string, string>(StringComparer.Ordinal) { [MetaKeys.LastAnalyzed] = "test" };
        await store.SaveAsync(graph, Array.Empty<FileRecord>(), meta);
        return new TestGraph(directory, store);
    }

    public async ValueTask DisposeAsync()
    {
        await Store.DisposeAsync();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }
}

/// <summary>Fluent-ish builders for synthetic graphs used across the v0.3.0 tool tests.</summary>
internal static class Build
{
    public static SymbolNode Node(NodeKind kind, string fqn, string? file = null, int spanStart = 0) =>
        SymbolNode.Create(kind, LastSegment(fqn), fqn, file, file is null ? null : new SourceSpan(spanStart, spanStart + 1));

    public static void Edge(CodeGraph graph, SymbolNode source, SymbolNode target, RelationshipKind kind) =>
        graph.AddEdge(new RelationshipEdge(source.Id, target.Id, kind));

    /// <summary>
    /// The Shapes graph mirrors the Roslyn fixture: IShape (with Area) &lt;- ShapeBase (Implements) &lt;-
    /// Circle/Square (Inherits), each with an Area() override. Single project "Lib".
    /// </summary>
    public static CodeGraph Shapes()
    {
        var graph = new CodeGraph();
        const string dir = "C:/repo/Lib/";
        var project = Node(NodeKind.Project, "Lib", dir + "Lib.csproj");
        var ns = Node(NodeKind.Namespace, "Fixture.Lib");
        var ishape = Node(NodeKind.Interface, "Fixture.Lib.IShape", dir + "IShape.cs");
        var area = Node(NodeKind.Method, "Fixture.Lib.IShape.Area()", dir + "IShape.cs");
        var shapeBase = Node(NodeKind.Class, "Fixture.Lib.ShapeBase", dir + "ShapeBase.cs");
        var baseArea = Node(NodeKind.Method, "Fixture.Lib.ShapeBase.Area()", dir + "ShapeBase.cs");
        var circle = Node(NodeKind.Class, "Fixture.Lib.Circle", dir + "Circle.cs");
        var circleArea = Node(NodeKind.Method, "Fixture.Lib.Circle.Area()", dir + "Circle.cs");
        var square = Node(NodeKind.Class, "Fixture.Lib.Square", dir + "Square.cs");
        var squareArea = Node(NodeKind.Method, "Fixture.Lib.Square.Area()", dir + "Square.cs");

        foreach (var node in new[] { project, ns, ishape, area, shapeBase, baseArea, circle, circleArea, square, squareArea })
        {
            graph.AddNode(node);
        }

        Edge(graph, project, ns, RelationshipKind.Contains);
        Edge(graph, ns, ishape, RelationshipKind.Contains);
        Edge(graph, ishape, area, RelationshipKind.Contains);
        Edge(graph, ns, shapeBase, RelationshipKind.Contains);
        Edge(graph, shapeBase, baseArea, RelationshipKind.Contains);
        Edge(graph, ns, circle, RelationshipKind.Contains);
        Edge(graph, circle, circleArea, RelationshipKind.Contains);
        Edge(graph, ns, square, RelationshipKind.Contains);
        Edge(graph, square, squareArea, RelationshipKind.Contains);

        Edge(graph, shapeBase, ishape, RelationshipKind.Implements);
        Edge(graph, circle, shapeBase, RelationshipKind.Inherits);
        Edge(graph, square, shapeBase, RelationshipKind.Inherits);

        return graph;
    }

    /// <summary>Two projects, Web depending on Core via two cross-project edges. Acyclic.</summary>
    public static CodeGraph WebAndCore()
    {
        var graph = new CodeGraph();
        var web = Node(NodeKind.Project, "Web", "C:/repo/Web/Web.csproj");
        var core = Node(NodeKind.Project, "Core", "C:/repo/Core/Core.csproj");
        var webA = Node(NodeKind.Class, "Web.A", "C:/repo/Web/A.cs");
        var webM = Node(NodeKind.Method, "Web.A.M()", "C:/repo/Web/A.cs");
        var coreB = Node(NodeKind.Class, "Core.B", "C:/repo/Core/B.cs");
        var coreN = Node(NodeKind.Method, "Core.B.N()", "C:/repo/Core/B.cs");
        foreach (var node in new[] { web, core, webA, webM, coreB, coreN })
        {
            graph.AddNode(node);
        }

        Edge(graph, web, webA, RelationshipKind.Contains);
        Edge(graph, webA, webM, RelationshipKind.Contains);
        Edge(graph, core, coreB, RelationshipKind.Contains);
        Edge(graph, coreB, coreN, RelationshipKind.Contains);
        Edge(graph, webM, coreN, RelationshipKind.Calls);        // cross-project
        Edge(graph, webA, coreB, RelationshipKind.References);   // cross-project
        return graph;
    }

    /// <summary>Two projects that reference each other — a project-level cycle.</summary>
    public static CodeGraph MutualProjects()
    {
        var graph = new CodeGraph();
        var alpha = Node(NodeKind.Project, "Alpha", "C:/repo/Alpha/Alpha.csproj");
        var beta = Node(NodeKind.Project, "Beta", "C:/repo/Beta/Beta.csproj");
        var a = Node(NodeKind.Class, "Alpha.A", "C:/repo/Alpha/A.cs");
        var b = Node(NodeKind.Class, "Beta.B", "C:/repo/Beta/B.cs");
        foreach (var node in new[] { alpha, beta, a, b })
        {
            graph.AddNode(node);
        }

        Edge(graph, alpha, a, RelationshipKind.Contains);
        Edge(graph, beta, b, RelationshipKind.Contains);
        Edge(graph, a, b, RelationshipKind.References);  // Alpha -> Beta
        Edge(graph, b, a, RelationshipKind.References);  // Beta -> Alpha
        return graph;
    }

    /// <summary>A product project plus a "FooTests" project whose test method calls into the product.</summary>
    public static CodeGraph ProductWithTests()
    {
        var graph = new CodeGraph();
        var foo = Node(NodeKind.Project, "Foo", "C:/repo/Foo/Foo.csproj");
        var fooTests = Node(NodeKind.Project, "FooTests", "C:/repo/FooTests/FooTests.csproj");
        var bar = Node(NodeKind.Class, "Foo.Bar", "C:/repo/Foo/Bar.cs");
        var doIt = Node(NodeKind.Method, "Foo.Bar.Do()", "C:/repo/Foo/Bar.cs");
        var testClass = Node(NodeKind.Class, "FooTests.BarTests", "C:/repo/FooTests/BarTests.cs");
        var testMethod = Node(NodeKind.Method, "FooTests.BarTests.Do_Works()", "C:/repo/FooTests/BarTests.cs");
        foreach (var node in new[] { foo, fooTests, bar, doIt, testClass, testMethod })
        {
            graph.AddNode(node);
        }

        Edge(graph, foo, bar, RelationshipKind.Contains);
        Edge(graph, bar, doIt, RelationshipKind.Contains);
        Edge(graph, fooTests, testClass, RelationshipKind.Contains);
        Edge(graph, testClass, testMethod, RelationshipKind.Contains);
        Edge(graph, testMethod, doIt, RelationshipKind.Calls);  // the test exercises Foo.Bar.Do()
        return graph;
    }

    private static string LastSegment(string fqn)
    {
        int paren = fqn.IndexOf('(', StringComparison.Ordinal);
        string head = paren >= 0 ? fqn[..paren] : fqn;
        int dot = head.LastIndexOf('.');
        return dot >= 0 ? head[(dot + 1)..] : head;
    }
}
