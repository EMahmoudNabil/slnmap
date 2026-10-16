using Slnmap.Core.Graph;
using Slnmap.Core.Storage;
using Slnmap.Mcp;
using Slnmap.Storage;
using Xunit;

namespace Slnmap.Tests;

/// <summary>
/// A synthetic graph with more than the find_symbol cap of matches, plus a unique one, to verify the
/// count is reported honestly: "20+ … refine your query" when capped, an exact count when not.
/// </summary>
public sealed class FindSymbolCapFixture : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "slnmap-cap-tests", Guid.NewGuid().ToString("N"));

    public SqliteGraphStore Store { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        var graph = new CodeGraph();
        graph.AddNode(SymbolNode.Create(NodeKind.Project, "P", "P"));
        for (int i = 0; i < 25; i++) // 25 > FindLimit (20)
        {
            graph.AddNode(SymbolNode.Create(
                NodeKind.Method, $"WidgetMethod{i:D2}", $"Ns.WidgetMethod{i:D2}()", "file.cs", new SourceSpan(0, 1)));
        }

        graph.AddNode(SymbolNode.Create(NodeKind.Class, "Lonely", "Ns.Lonely", "file.cs", new SourceSpan(0, 1)));

        Store = new SqliteGraphStore(Path.Combine(_directory, "cap.db"));
        var meta = new Dictionary<string, string>(StringComparer.Ordinal) { [MetaKeys.LastAnalyzed] = "test" };
        await Store.SaveAsync(graph, Array.Empty<FileRecord>(), meta);
    }

    public async Task DisposeAsync()
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

public sealed class FindSymbolCapTests : IClassFixture<FindSymbolCapFixture>
{
    private readonly SlnmapQueries _queries;

    public FindSymbolCapTests(FindSymbolCapFixture fixture) => _queries = new SlnmapQueries(fixture.Store);

    [Fact]
    public async Task FindSymbol_OverCap_ReportsPlusAndRefineHint_NotAFalseTotal()
    {
        string result = await _queries.FindSymbolAsync("Widget", null); // 25 matches, cap is 20

        Assert.Contains("20+ matches", result, StringComparison.Ordinal);
        Assert.Contains("refine your query", result, StringComparison.Ordinal);
        // Must NOT present the cap (or the true count) as a definitive total.
        Assert.DoesNotContain("20 match(es)", result, StringComparison.Ordinal);
        Assert.DoesNotContain("25 match(es)", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindSymbol_UnderCap_ReportsExactCount()
    {
        string result = await _queries.FindSymbolAsync("Lonely", null); // exactly 1 match

        Assert.Contains("1 match(es) for 'Lonely':", result, StringComparison.Ordinal);
        Assert.DoesNotContain("20+", result, StringComparison.Ordinal);
    }
}

/// <summary>
/// A kind-filtered find_symbol search is narrowed at the source (GetNodesByKindAsync, not a
/// post-hoc substring page), so it earns a higher cap (50) than an unfiltered one (20) before
/// "refine your query" kicks in. 35 Namespace matches — between the two caps — must all be shown;
/// 55 must still be capped, at 50, not 20.
/// </summary>
public sealed class FindSymbolKindCapFixture : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "slnmap-kind-cap-tests", Guid.NewGuid().ToString("N"));

    public SqliteGraphStore Store { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        var graph = new CodeGraph();
        for (int i = 0; i < 35; i++) // > unfiltered cap (20), < kind-filtered cap (50)
        {
            graph.AddNode(SymbolNode.Create(NodeKind.Namespace, $"App.Area{i:D2}", $"App.Area{i:D2}"));
        }

        for (int i = 0; i < 55; i++) // > kind-filtered cap (50)
        {
            graph.AddNode(SymbolNode.Create(NodeKind.Class, $"OverflowClass{i:D2}", $"App.OverflowClass{i:D2}", "file.cs", new SourceSpan(0, 1)));
        }

        Store = new SqliteGraphStore(Path.Combine(_directory, "kind-cap.db"));
        var meta = new Dictionary<string, string>(StringComparer.Ordinal) { [MetaKeys.LastAnalyzed] = "test" };
        await Store.SaveAsync(graph, Array.Empty<FileRecord>(), meta);
    }

    public async Task DisposeAsync()
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

public sealed class FindSymbolKindCapTests : IClassFixture<FindSymbolKindCapFixture>
{
    private readonly SlnmapQueries _queries;

    public FindSymbolKindCapTests(FindSymbolKindCapFixture fixture) => _queries = new SlnmapQueries(fixture.Store);

    [Fact]
    public async Task FindSymbol_KindFiltered_BetweenTheTwoCaps_ReportsExactCountNotCapped()
    {
        string result = await _queries.FindSymbolAsync("App.Area", "Namespace"); // 35 matches

        Assert.Contains("35 match(es) for 'App.Area':", result, StringComparison.Ordinal);
        Assert.DoesNotContain("refine your query", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindSymbol_KindFiltered_OverTheHigherCap_CapsAt50NotAt20()
    {
        string result = await _queries.FindSymbolAsync("OverflowClass", "Class"); // 55 matches

        Assert.Contains("50+ matches", result, StringComparison.Ordinal);
        Assert.Contains("refine your query", result, StringComparison.Ordinal);
        Assert.DoesNotContain("20+ matches", result, StringComparison.Ordinal);
    }
}
