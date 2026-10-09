using System.Globalization;
using System.Text;
using Slnmap.Core.Graph;
using Slnmap.Core.Storage;

namespace Slnmap.Mcp;

public sealed partial class SlnmapQueries
{
    private const int AttributeUsagesCap = 50;
    private const int AttributeCandidatesCap = 15;

    /// <summary>
    /// v0.14.0 (get_attribute_usages, docs/EXPANSION-SPECS.md §10): where an attribute is applied.
    /// Accepts an FQN or a short name, with or without the "Attribute" suffix or brackets
    /// ("[Authorize]", "Authorize", "Microsoft.AspNetCore.Authorization.AuthorizeAttribute"). A
    /// short name matching several attribute types lists them instead of guessing.
    /// </summary>
    public async Task<string> GetAttributeUsagesAsync(string attribute, CancellationToken cancellationToken = default)
    {
        if (await NotAnalyzedAsync(cancellationToken).ConfigureAwait(false) is { } notReady)
        {
            return notReady;
        }

        if (string.IsNullOrWhiteSpace(attribute))
        {
            return ToolFailure.MissingParameter(
                "attribute",
                ["attribute"],
                "Provide an attribute in 'attribute' — a short name or a fully qualified name, e.g. {\"attribute\": \"Authorize\"}.");
        }

        var meta = await _store.GetMetaAsync(cancellationToken).ConfigureAwait(false);
        if (!meta.TryGetValue(MetaKeys.SchemaVersion, out var schema)
            || !int.TryParse(schema, NumberStyles.Integer, CultureInfo.InvariantCulture, out int schemaVersion)
            || schemaVersion < 2)
        {
            return "This graph was built before attribute usages were recorded (schema v1) — re-run 'slnmap analyze' "
                + "with this slnmap version, then call get_attribute_usages again.";
        }

        string query = attribute.Trim().TrimStart('[').TrimEnd(']').Trim();
        var summary = await _store.GetAttributeSummaryAsync(cancellationToken).ConfigureAwait(false);
        var matches = summary.Where(s => AttributeMatches(s.AttributeFqn, query)).ToList();

        if (matches.Count == 0)
        {
            var near = summary
                .Where(s => ShortName(s.AttributeFqn).Contains(StripAttributeSuffix(LastSegment(query)), StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(s => s.Count)
                .Take(AttributeCandidatesCap)
                .ToList();
            var builder = new StringBuilder();
            builder.AppendLine($"No usages of [{query}] in the graph ({summary.Count} distinct attribute type(s) are applied in this solution).");
            if (near.Count > 0)
            {
                builder.AppendLine("Similar attributes:");
                foreach (var (fqn, count) in near)
                {
                    builder.AppendLine($"  {fqn} ({count})");
                }
            }

            return builder.ToString().TrimEnd();
        }

        if (matches.Count > 1)
        {
            var builder = new StringBuilder();
            builder.AppendLine($"[{query}] matches {matches.Count} attribute types — pass the fully qualified name of the one you mean:");
            foreach (var (fqn, count) in matches.OrderByDescending(m => m.Count).Take(AttributeCandidatesCap))
            {
                builder.AppendLine($"  {fqn} ({count} usage(s))");
            }

            if (matches.Count > AttributeCandidatesCap)
            {
                builder.AppendLine($"  ...and {matches.Count - AttributeCandidatesCap} more.");
            }

            return builder.ToString().TrimEnd();
        }

        string attributeFqn = matches[0].AttributeFqn;
        var usages = await _store.GetAttributeUsagesAsync(attributeFqn, cancellationToken).ConfigureAwait(false);
        var targets = (await _store.GetNodesByIdsAsync(usages.Select(u => u.TargetId), cancellationToken).ConfigureAwait(false))
            .ToDictionary(n => n.Id, StringComparer.Ordinal);
        var attributor = ProjectAttributor.From(await _store.GetNodesByKindAsync(NodeKind.Project, cancellationToken).ConfigureAwait(false));
        var resolver = new LineResolver();

        var result = new StringBuilder();
        string label = $"[{StripAttributeSuffix(ShortName(attributeFqn))}] ({attributeFqn})";
        bool capped = usages.Count > AttributeUsagesCap;
        result.AppendLine(capped
            ? $"{AttributeUsagesCap}+ usage(s) of {label} (showing first {AttributeUsagesCap} of {usages.Count}):"
            : $"{usages.Count} usage(s) of {label}:");

        foreach (var group in usages
            .Take(AttributeUsagesCap)
            .GroupBy(u => attributor.ProjectOf(u.FilePath) ?? "(unknown project)")
            .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            result.AppendLine($"{group.Key}:");
            foreach (var usage in group)
            {
                string target = targets.TryGetValue(usage.TargetId, out var node)
                    ? node.Kind == NodeKind.Project ? $"[assembly] {node.Name}" : $"[{node.Kind}] {node.Fqn}"
                    : "(declaration not in the graph)";
                result.AppendLine($"  {target} — {usage.FilePath}:{resolver.LineOf(usage.FilePath, usage.SpanStart)}");
            }
        }

        if (attributeFqn.StartsWith("unresolved:", StringComparison.Ordinal))
        {
            result.AppendLine("note: this attribute's type did not resolve at analyze time (a missing reference or SDK?) — matched by the name as written.");
        }

        result.AppendLine("note: attributes on parameters, return values and accessors are listed against their member.");
        return result.ToString().TrimEnd();
    }

    private static bool AttributeMatches(string fqn, string query)
    {
        if (fqn.Equals(query, StringComparison.Ordinal))
        {
            return true;
        }

        string bareQuery = StripAttributeSuffix(LastSegment(query));
        string bareFqn = StripAttributeSuffix(ShortName(fqn));
        bool queryIsQualified = query.Contains('.', StringComparison.Ordinal);

        // A qualified query must match the FQN (with or without the Attribute suffix); a short one
        // matches the type's own name, case-insensitively.
        return queryIsQualified
            ? StripAttributeSuffix(StripGenerics(fqn)).Equals(StripAttributeSuffix(query), StringComparison.Ordinal)
            : bareFqn.Equals(bareQuery, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>"Ns.Outer.FooAttribute&lt;T&gt;" → "FooAttribute"; "unresolved:Foo" → "Foo".</summary>
    private static string ShortName(string fqn)
    {
        string name = fqn.StartsWith("unresolved:", StringComparison.Ordinal) ? fqn["unresolved:".Length..] : fqn;
        return LastSegment(StripGenerics(name));
    }

    private static string StripGenerics(string name)
    {
        int angle = name.IndexOf('<', StringComparison.Ordinal);
        return angle >= 0 ? name[..angle] : name;
    }

    private static string StripAttributeSuffix(string name) =>
        name.Length > "Attribute".Length && name.EndsWith("Attribute", StringComparison.Ordinal)
            ? name[..^"Attribute".Length]
            : name;
}
