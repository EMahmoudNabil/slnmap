using System.Globalization;
using System.Text;
using Slnmap.Core.Graph;
using Slnmap.Core.Storage;

namespace Slnmap.Mcp;

public sealed partial class SlnmapQueries
{
    private const int ExternalCallersCap = 30;
    private const int ExternalTargetsShown = 15;

    /// <summary>
    /// v0.14.0 (find_callers_of_external, docs/EXPANSION-SPECS.md §7): where the solution calls
    /// into a package or framework, by namespace prefix ("Microsoft.EntityFrameworkCore") or
    /// assembly name. Counts first — distinct callers, call sites, the most-called targets — then
    /// the callers grouped by project with file:line and what each calls.
    /// </summary>
    public async Task<string> FindCallersOfExternalAsync(string target, string? project, CancellationToken cancellationToken = default)
    {
        if (await NotAnalyzedAsync(cancellationToken).ConfigureAwait(false) is { } notReady)
        {
            return notReady;
        }

        if (string.IsNullOrWhiteSpace(target))
        {
            return ToolFailure.MissingParameter(
                "target",
                ["target", "project"],
                "Provide a namespace prefix or assembly name in 'target', e.g. {\"target\": \"Microsoft.EntityFrameworkCore\"}.");
        }

        var meta = await _store.GetMetaAsync(cancellationToken).ConfigureAwait(false);
        if (!meta.TryGetValue(MetaKeys.SchemaVersion, out var schema)
            || !int.TryParse(schema, NumberStyles.Integer, CultureInfo.InvariantCulture, out int schemaVersion)
            || schemaVersion < 2)
        {
            return "This graph was built before external calls were recorded (schema v1) — re-run 'slnmap analyze' "
                + "with this slnmap version, then call find_callers_of_external again.";
        }

        var projectNodes = await _store.GetNodesByKindAsync(NodeKind.Project, cancellationToken).ConfigureAwait(false);
        string? projectFilter = null;
        if (!string.IsNullOrWhiteSpace(project) && !project.Trim().Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            projectFilter = projectNodes.Select(p => p.Name).FirstOrDefault(n => n.Equals(project.Trim(), StringComparison.OrdinalIgnoreCase));
            if (projectFilter is null)
            {
                return ToolFailure.InvalidParameter(
                    "project",
                    ["target", "project"],
                    $"Unknown project '{project}'. Use 'all' or one of: {string.Join(", ", projectNodes.Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal))}.");
            }
        }

        string prefix = target.Trim();
        var attributor = ProjectAttributor.From(projectNodes);
        var calls = (await _store.GetExternalCallsAsync(prefix, cancellationToken).ConfigureAwait(false))
            .Where(c => projectFilter is null || attributor.ProjectOf(c.FilePath) == projectFilter)
            .ToList();

        if (calls.Count == 0)
        {
            var namespaces = await _store.GetExternalNamespacesAsync(cancellationToken).ConfigureAwait(false);
            var similar = namespaces
                .Where(n => n.Namespace.Contains(prefix, StringComparison.OrdinalIgnoreCase)
                    || prefix.Contains(n.Namespace, StringComparison.OrdinalIgnoreCase))
                .Take(ExternalTargetsShown)
                .ToList();
            var none = new StringBuilder();
            none.AppendLine($"0 calls into '{prefix}'{(projectFilter is null ? string.Empty : $" from {projectFilter}")} "
                + "(matched as a namespace prefix or an assembly name).");
            var suggestions = similar.Count > 0 ? similar : namespaces.Take(ExternalTargetsShown).ToList();
            if (suggestions.Count > 0)
            {
                none.AppendLine(similar.Count > 0 ? "Similar namespaces:" : "Most-called external namespaces in this solution:");
                foreach (var (ns, pairs) in suggestions)
                {
                    none.AppendLine($"  {ns} ({pairs} caller/target pair(s))");
                }
            }

            return none.ToString().TrimEnd();
        }

        // Generated files (EF migration designers, *.g.cs, obj/) are counted, never listed: on a
        // real solution they can hold 98% of all EF call sites and drown out the code people wrote.
        var generated = calls.Where(c => IsGeneratedFile(c.FilePath)).ToList();
        calls = calls.Where(c => !IsGeneratedFile(c.FilePath)).ToList();

        int callSites = calls.Sum(c => c.CallCount);
        var byCaller = calls.GroupBy(c => c.CallerId, StringComparer.Ordinal).ToList();
        var callers = (await _store.GetNodesByIdsAsync(byCaller.Select(g => g.Key), cancellationToken).ConfigureAwait(false))
            .ToDictionary(n => n.Id, StringComparer.Ordinal);

        var builder = new StringBuilder();
        builder.AppendLine(
            $"{callSites} call site(s) into '{prefix}' from {byCaller.Count} member(s) across "
            + $"{byCaller.Select(g => attributor.ProjectOf(g.First().FilePath) ?? "(unknown project)").Distinct(StringComparer.Ordinal).Count()} project(s):");
        if (generated.Count > 0)
        {
            builder.AppendLine(
                $"(+ {generated.Sum(c => c.CallCount)} more call site(s) in {generated.Select(c => c.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).Count()} "
                + "generated file(s) — e.g. EF migration designers — counted, not listed)");
        }

        if (byCaller.Count == 0)
        {
            return builder.ToString().TrimEnd();
        }

        builder.AppendLine("Most-called targets:");
        foreach (var hot in calls
            .GroupBy(c => c.TargetFqn, StringComparer.Ordinal)
            .Select(g => (Target: g.Key, Calls: g.Sum(c => c.CallCount), Callers: g.Count()))
            .OrderByDescending(t => t.Calls)
            .ThenBy(t => t.Target, StringComparer.Ordinal)
            .Take(5))
        {
            builder.AppendLine($"  {hot.Target} — {hot.Calls} call(s) from {hot.Callers} member(s)");
        }

        bool capped = byCaller.Count > ExternalCallersCap;
        builder.AppendLine(capped
            ? $"Callers (showing first {ExternalCallersCap} of {byCaller.Count} — filter by project or a narrower namespace):"
            : "Callers:");
        var resolver = new LineResolver();
        foreach (var group in byCaller
            .Take(ExternalCallersCap)
            .GroupBy(g => attributor.ProjectOf(g.First().FilePath) ?? "(unknown project)")
            .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            builder.AppendLine($"{group.Key}:");
            foreach (var caller in group)
            {
                var first = caller.OrderBy(c => c.SpanStart).First();
                string label = callers.TryGetValue(caller.Key, out var node) ? $"[{node.Kind}] {node.Fqn}" : "(member not in the graph)";
                var targets = caller
                    .OrderByDescending(c => c.CallCount)
                    .Select(c => c.CallCount > 1 ? $"{ShortTarget(c.TargetFqn)} ×{c.CallCount}" : ShortTarget(c.TargetFqn))
                    .ToList();
                string calledList = targets.Count <= 4
                    ? string.Join(", ", targets)
                    : string.Join(", ", targets.Take(4)) + $", +{targets.Count - 4} more";
                builder.AppendLine($"  {label} — {first.FilePath}:{resolver.LineOf(first.FilePath, first.SpanStart)} → {calledList}");
            }
        }

        builder.AppendLine("note: calls and object creations are recorded per (calling member, target) with the first site's line; "
            + "property reads and delegate invocations are not calls.");
        return builder.ToString().TrimEnd();
    }

    /// <summary>"Ns.Type.Method(Args)" → "Type.Method" — compact for the per-caller list (the hot list keeps full FQNs).</summary>
    private static string ShortTarget(string fqn)
    {
        int paren = fqn.IndexOf('(', StringComparison.Ordinal);
        string head = paren >= 0 ? fqn[..paren] : fqn;
        int generic = head.IndexOf('<', StringComparison.Ordinal);
        string scan = generic >= 0 ? head[..generic] : head;
        int lastDot = scan.LastIndexOf('.');
        int typeDot = lastDot > 0 ? scan.LastIndexOf('.', lastDot - 1) : -1;
        return typeDot >= 0 ? head[(typeDot + 1)..] : head;
    }
}
