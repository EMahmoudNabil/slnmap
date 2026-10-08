using System.Globalization;
using System.Text;
using Slnmap.Core.Graph;
using Slnmap.Core.Storage;

namespace Slnmap.Mcp;

public sealed partial class SlnmapQueries
{
    private const int UnusedSymbolsCap = 50;
    private const int FrameworkDerivedCap = 10;

    /// <summary>Kinds searched when no kind filter is given — every symbol a person would delete.</summary>
    private static readonly NodeKind[] DefaultUnusedKinds =
    [
        NodeKind.Class, NodeKind.Interface, NodeKind.Struct, NodeKind.Record, NodeKind.Enum, NodeKind.Delegate,
        NodeKind.Method, NodeKind.Property, NodeKind.Field, NodeKind.Event,
    ];

    /// <summary>
    /// Kinds a kind filter may name. Not Constructor: <c>new T()</c> is recorded as a reference to
    /// the type and constructor chaining is not an edge, so no constructor ever has an incoming
    /// edge — every one would be reported (v0.14.0 QA finding 2). A type's own usage covers it.
    /// </summary>
    private static readonly NodeKind[] FilterableUnusedKinds = DefaultUnusedKinds;

    /// <summary>
    /// v0.14.0 (find_unused_symbols, docs/EXPANSION-SPECS.md §5): public/internal symbols with no
    /// incoming dependency of any kind. Leads with the caveat — zero static references is not
    /// dead code — then removes what the graph can prove is reached another way: overrides and
    /// interface implementations (member_flags), entry points, test projects, generated files,
    /// controller/Razor Page handlers by name (stated). Grouped by project with file:line.
    /// </summary>
    public async Task<string> FindUnusedSymbolsAsync(string? scope, string? kind, CancellationToken cancellationToken = default)
    {
        if (await NotAnalyzedAsync(cancellationToken).ConfigureAwait(false) is { } notReady)
        {
            return notReady;
        }

        var meta = await _store.GetMetaAsync(cancellationToken).ConfigureAwait(false);
        if (!meta.TryGetValue(MetaKeys.SchemaVersion, out var schema)
            || !int.TryParse(schema, NumberStyles.Integer, CultureInfo.InvariantCulture, out int schemaVersion)
            || schemaVersion < 2)
        {
            return "This graph was built before accessibility data existed (schema v1) — re-run 'slnmap analyze' "
                + "with this slnmap version, then call find_unused_symbols again.";
        }

        NodeKind[] kinds = DefaultUnusedKinds;
        if (!string.IsNullOrWhiteSpace(kind))
        {
            var match = FilterableUnusedKinds.Where(k => k.ToString().Equals(kind.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
            if (match.Length == 0)
            {
                return ToolFailure.InvalidParameter(
                    "kind",
                    ["scope", "kind"],
                    $"Unknown kind '{kind}'. Valid kinds: {string.Join(", ", FilterableUnusedKinds)}.");
            }

            kinds = match;
        }

        var projectNodes = await _store.GetNodesByKindAsync(NodeKind.Project, cancellationToken).ConfigureAwait(false);
        string? projectFilter = null;
        if (!string.IsNullOrWhiteSpace(scope) && !scope.Trim().Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            projectFilter = projectNodes.Select(p => p.Name).FirstOrDefault(n => n.Equals(scope.Trim(), StringComparison.OrdinalIgnoreCase));
            if (projectFilter is null)
            {
                return ToolFailure.InvalidParameter(
                    "scope",
                    ["scope", "kind"],
                    $"Unknown project '{scope}'. Use 'all' or one of: {string.Join(", ", projectNodes.Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal))}.");
            }
        }

        var attributor = ProjectAttributor.From(projectNodes);
        var testProjects = projectNodes
            .Select(p => p.Name)
            .Where(n => n.Contains("Test", StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.Ordinal);

        var candidates = await _store.GetUnreferencedNodesAsync(kinds, ["Public", "Internal"], cancellationToken).ConfigureAwait(false);
        var excluded = new Dictionary<string, int>(StringComparer.Ordinal);
        var unused = new List<SymbolNode>();
        var frameworkDerived = new List<SymbolNode>();
        foreach (var node in candidates)
        {
            string? project = attributor.ProjectOf(node.FilePath);
            if (projectFilter is not null && project != projectFilter)
            {
                continue;
            }

            if (ExclusionReason(node, project, testProjects) is { } reason)
            {
                excluded[reason] = excluded.GetValueOrDefault(reason) + 1;
                continue;
            }

            // Lower confidence, listed separately rather than hidden: a type deriving from or
            // implementing a framework type is usually found by reflection/DI at runtime.
            if (node.MemberFlags is "external-base")
            {
                frameworkDerived.Add(node);
                continue;
            }

            unused.Add(node);
        }

        string scopeLabel = projectFilter ?? "all";
        string kindLabel = kinds.Length == DefaultUnusedKinds.Length ? "types and members" : string.Join("/", kinds);
        var builder = new StringBuilder();
        builder.AppendLine(
            "CAVEAT: zero static references != dead code. Reflection/serialization targets (e.g. DTO properties), "
            + "DI-only types, framework-invoked members, public API consumed outside this solution, and members "
            + "used only from Razor/.cshtml markup appear here as false positives. Verify before deleting.");

        if (unused.Count == 0)
        {
            builder.AppendLine($"0 public/internal {kindLabel} with no incoming references (scope={scopeLabel}).");
        }
        else
        {
            bool capped = unused.Count > UnusedSymbolsCap;
            builder.AppendLine(capped
                ? $"{UnusedSymbolsCap}+ public/internal {kindLabel} with no incoming references (scope={scopeLabel}, showing first {UnusedSymbolsCap} of {unused.Count} — refine by scope or kind):"
                : $"{unused.Count} public/internal {kindLabel} with no incoming references (scope={scopeLabel}):");
            await AppendProjectGroupedAsync(builder, unused, UnusedSymbolsCap, cancellationToken).ConfigureAwait(false);
        }

        if (frameworkDerived.Count > 0)
        {
            int shown = Math.Min(frameworkDerived.Count, FrameworkDerivedCap);
            builder.AppendLine(
                $"Lower confidence — {frameworkDerived.Count} type(s) deriving from or implementing a framework type, usually "
                + "discovered by reflection/DI at runtime (controllers, MediatR handlers, validators, EF configurations, "
                + $"migrations, hosted services){(shown < frameworkDerived.Count ? $", first {shown}" : string.Empty)}:");
            await AppendProjectGroupedAsync(builder, frameworkDerived, FrameworkDerivedCap, cancellationToken).ConfigureAwait(false);
        }

        if (excluded.Count > 0)
        {
            builder.AppendLine(
                "Excluded as reached another way: "
                + string.Join(", ", excluded.OrderByDescending(e => e.Value).Select(e => $"{e.Value} {e.Key}"))
                + ".");
        }

        builder.AppendLine("Only Public/Internal declared accessibility is searched; private and protected members are not.");
        return builder.ToString().TrimEnd();
    }

    /// <summary>Why a symbol with no incoming edges is still not reported, or null to report it.</summary>
    private static string? ExclusionReason(SymbolNode node, string? project, IReadOnlySet<string> testProjects)
    {
        if (node.MemberFlags is { } flags)
        {
            if (flags.Contains("interface-impl", StringComparison.Ordinal))
            {
                return "interface implementation(s)";
            }

            if (flags.Contains("override", StringComparison.Ordinal))
            {
                return "override(s)";
            }
        }

        if (project is not null && testProjects.Contains(project))
        {
            return "in test projects (name contains \"Test\")";
        }

        if (node.FilePath is { } file && (IsGeneratedFile(file) || project is null))
        {
            // Outside every project directory means SDK/package-injected source (e.g. the test
            // SDK's AutoGeneratedProgram from the NuGet cache), not code anyone here wrote.
            return "in generated or package-injected files";
        }

        if ((node.Kind == NodeKind.Method && (node.Name == "Main" || node.Fqn.Contains("<top-level-statements-entry-point>", StringComparison.Ordinal)))
            || (node.Kind == NodeKind.Class && node.Name == "Program"))
        {
            return "entry point(s)";
        }

        // Name-based, stated: conventionally-routed controller actions and Razor Page handlers are
        // invoked by MVC with no static caller (attribute-routed actions already count as used via
        // their endpoint's HandledBy edge).
        if (node.Kind == NodeKind.Method && ContainingTypeName(node.Fqn) is { } typeName)
        {
            if (typeName.EndsWith("Controller", StringComparison.Ordinal))
            {
                return "controller action(s) (by class name)";
            }

            if (typeName.EndsWith("Model", StringComparison.Ordinal) && node.Name.StartsWith("On", StringComparison.Ordinal))
            {
                return "Razor Page handler(s) (On* on a *Model class)";
            }
        }

        return null;
    }

    private static bool IsGeneratedFile(string file)
    {
        string normalized = file.Replace('\\', '/');
        return normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>"Ns.Type.Member(args)" → "Type"; null when there is no containing type segment.</summary>
    private static string? ContainingTypeName(string fqn)
    {
        int paren = fqn.IndexOf('(', StringComparison.Ordinal);
        string head = paren >= 0 ? fqn[..paren] : fqn;
        int lastDot = head.LastIndexOf('.');
        if (lastDot <= 0)
        {
            return null;
        }

        string typePath = head[..lastDot];
        int generic = typePath.IndexOf('<', StringComparison.Ordinal);
        if (generic >= 0)
        {
            typePath = typePath[..generic];
        }

        int typeDot = typePath.LastIndexOf('.');
        return typeDot >= 0 ? typePath[(typeDot + 1)..] : typePath;
    }
}
