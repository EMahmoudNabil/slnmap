using Slnmap.Core.Graph;
using Xunit;

namespace Slnmap.Tests;

public sealed class SymbolSourceTests
{
    [Fact]
    public async Task ReturnsSnippetWithLineNumbers()
    {
        string file = TempSource("line1\nline2\npublic class Widget\n{\n    public void Go() { }\n}\nlast\n");
        try
        {
            int start = await OffsetOfAsync(file, "public class Widget");
            var graph = OneNode(NodeKind.Class, "Ns.Widget", file, start, start + 19);

            await using var g = await TestGraph.CreateAsync(graph);
            string result = await g.Queries.GetSymbolSourceAsync("Ns.Widget", 1);

            Assert.Contains("public class Widget", result, StringComparison.Ordinal);
            Assert.Contains("Ns.Widget", result, StringComparison.Ordinal);
            Assert.Contains(" | ", result, StringComparison.Ordinal); // line-numbered gutter
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task LongSpan_CapsAt120Lines()
    {
        string body = string.Concat(Enumerable.Range(1, 300).Select(i => $"// line {i}\n"));
        string file = TempSource(body);
        try
        {
            var graph = OneNode(NodeKind.Class, "Ns.Big", file, 0, body.Length);

            await using var g = await TestGraph.CreateAsync(graph);
            string result = await g.Queries.GetSymbolSourceAsync("Ns.Big", 0);

            Assert.Contains("capped at 120 lines", result, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task NamespaceWithNoLocation_ReturnsActionableMessage()
    {
        await using var g = await TestGraph.CreateAsync(Build.Shapes());

        string result = await g.Queries.GetSymbolSourceAsync("Fixture.Lib", 5);

        Assert.Contains("no single source location", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingFile_ReturnsActionableMessage()
    {
        string missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".cs");
        var graph = OneNode(NodeKind.Class, "Ns.Gone", missing, 0, 1);

        await using var g = await TestGraph.CreateAsync(graph);
        string result = await g.Queries.GetSymbolSourceAsync("Ns.Gone", 5);

        Assert.Contains("Source file not found", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownFqn_ReturnsNotFound()
    {
        await using var g = await TestGraph.CreateAsync(Build.Shapes());

        string result = await g.Queries.GetSymbolSourceAsync("Ns.Nope", 5);

        Assert.Contains("No symbol with FQN", result, StringComparison.Ordinal);
    }

    private static CodeGraph OneNode(NodeKind kind, string fqn, string file, int start, int end)
    {
        var graph = new CodeGraph();
        graph.AddNode(SymbolNode.Create(kind, fqn.Split('.')[^1], fqn, file, new SourceSpan(start, end)));
        return graph;
    }

    private static string TempSource(string content)
    {
        string path = Path.Combine(Path.GetTempPath(), "slnmap-src-" + Guid.NewGuid().ToString("N") + ".cs");
        File.WriteAllText(path, content);
        return path;
    }

    private static async Task<int> OffsetOfAsync(string file, string marker)
    {
        string text = await File.ReadAllTextAsync(file);
        return text.IndexOf(marker, StringComparison.Ordinal);
    }
}
