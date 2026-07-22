using System.Text;
using Slnmap.Core.Graph;
using Slnmap.Core.Storage;

namespace Slnmap.Mcp;

public sealed partial class SlnmapQueries
{
    private const int TestsCap = 60;

    /// <summary>
    /// Finds the test members that transitively reach a symbol: incoming Calls/References dependents
    /// (depth 5) filtered to test projects. Heuristic: a project is a test project when its name
    /// contains "Test" (case-insensitive) — stated in the output, since the graph stores no package
    /// references to detect a test framework directly.
    /// </summary>
    public async Task<string> FindTestsForSymbolAsync(string fqn, CancellationToken cancellationToken = default)
    {
        if (await NotAnalyzedAsync(cancellationToken).ConfigureAwait(false) is { } notReady)
        {
            return notReady;
        }

        var matches = await _store.GetNodesByFqnAsync(fqn, cancellationToken).ConfigureAwait(false);
        if (matches.Count == 0)
        {
            return await NotFoundAsync(fqn, cancellationToken).ConfigureAwait(false);
        }

        var projectNodes = await _store.GetNodesByKindAsync(NodeKind.Project, cancellationToken).ConfigureAwait(false);
        var testProjects = projectNodes
            .Select(p => p.Name)
            .Where(n => n.Contains("Test", StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.Ordinal);
        if (testProjects.Count == 0)
        {
            return "No test projects detected (heuristic: project name contains \"Test\"). "
                + $"Projects in solution: {string.Join(", ", projectNodes.Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal))}.";
        }

        var attributor = ProjectAttributor.From(projectNodes);
        var reached = new Dictionary<string, SymbolNode>(StringComparer.Ordinal);
        foreach (var target in matches)
        {
            foreach (var node in await _store.TraverseAsync(target.Id, EdgeDirection.Incoming, 5, ImpactTraversalCap, cancellationToken).ConfigureAwait(false))
            {
                reached[node.Node.Id] = node.Node;
            }
        }

        var testMembers = reached.Values
            .Where(n => n.Kind is NodeKind.Method or NodeKind.Constructor or NodeKind.Property)
            .Where(n => attributor.ProjectOf(n.FilePath) is { } p && testProjects.Contains(p))
            .ToList();

        string label = matches.Count == 1 ? $"{matches[0].Kind} {matches[0].Fqn}" : $"symbols named {fqn}";
        if (testMembers.Count == 0)
        {
            return $"No tests found that reach {label} "
                + "(name-based detection; a test may still exercise it via reflection or integration without a static call).";
        }

        int distinctProjects = testMembers.Select(n => attributor.ProjectOf(n.FilePath)).Distinct(StringComparer.Ordinal).Count();
        var builder = new StringBuilder();
        builder.AppendLine($"Tests exercising {label}: {testMembers.Count} test member(s) across {distinctProjects} test project(s).");
        builder.AppendLine("(heuristic: project name contains \"Test\"; transitive callers up to depth 5)");
        await AppendProjectGroupedAsync(builder, testMembers, TestsCap, cancellationToken).ConfigureAwait(false);
        return builder.ToString().TrimEnd();
    }
}
