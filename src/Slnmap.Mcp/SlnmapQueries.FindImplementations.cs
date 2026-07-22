using System.Text;
using Slnmap.Core.Graph;
using Slnmap.Core.Storage;

namespace Slnmap.Mcp;

public sealed partial class SlnmapQueries
{
    private const int ImplementationsCap = 100;

    /// <summary>
    /// Lists the concrete types that implement an interface / derive from a base type, or the members
    /// that implement/override a given interface or virtual member. Transitive over Implements+Inherits.
    /// </summary>
    public async Task<string> FindImplementationsAsync(string fqn, CancellationToken cancellationToken = default)
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

        var results = new Dictionary<string, SymbolNode>(StringComparer.Ordinal);
        bool targetIsType = false;
        foreach (var target in matches)
        {
            if (target.Kind is NodeKind.Interface or NodeKind.Class or NodeKind.Struct or NodeKind.Record)
            {
                targetIsType = true;
                foreach (var implementer in await ClosureAsync(
                    target.Id, [RelationshipKind.Implements, RelationshipKind.Inherits], cancellationToken).ConfigureAwait(false))
                {
                    results[implementer.Id] = implementer;
                }
            }
            else if (target.Kind is NodeKind.Method or NodeKind.Property or NodeKind.Event)
            {
                await CollectMemberImplementationsAsync(target, results, cancellationToken).ConfigureAwait(false);
            }
        }

        string label = matches.Count == 1 ? $"{matches[0].Kind} {matches[0].Fqn}" : $"symbols named {fqn}";
        if (results.Count == 0)
        {
            string reason = targetIsType
                ? "no types in the solution implement or derive from it"
                : "no implementing/overriding members found (is it declared on an interface, or virtual/abstract?)";
            return $"0 implementations of {label}: {reason}.";
        }

        bool capped = results.Count > ImplementationsCap;
        var builder = new StringBuilder();
        builder.AppendLine(capped
            ? $"{ImplementationsCap}+ implementation(s) of {label} (direct + derived, showing first {ImplementationsCap} — refine):"
            : $"{results.Count} implementation(s) of {label} (direct + derived):");
        await AppendProjectGroupedAsync(builder, results.Values.ToList(), ImplementationsCap, cancellationToken).ConfigureAwait(false);

        if (matches.Count > 1)
        {
            builder.AppendLine($"note: {matches.Count} symbols share this FQN ({string.Join(", ", matches.Select(m => m.Kind))}); results cover all.");
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// For a member declared on an interface or a virtual/abstract member on a base type, finds the
    /// matching member on every implementing/derived type by FQN suffix (the technique impact analysis
    /// uses). Explicit interface implementations collapse to the Type.Member form and are covered too.
    /// </summary>
    private async Task CollectMemberImplementationsAsync(
        SymbolNode member,
        Dictionary<string, SymbolNode> results,
        CancellationToken cancellationToken)
    {
        var containerEdges = await _store.GetEdgesAsync(member.Id, EdgeDirection.Incoming, RelationshipKind.Contains, cancellationToken).ConfigureAwait(false);
        var containers = await _store.GetNodesByIdsAsync(containerEdges.Select(e => e.SourceId), cancellationToken).ConfigureAwait(false);
        var containerType = containers.FirstOrDefault(c => c.Kind is NodeKind.Interface or NodeKind.Class);
        if (containerType is null || !member.Fqn.StartsWith(containerType.Fqn, StringComparison.Ordinal))
        {
            return;
        }

        string memberSuffix = member.Fqn[containerType.Fqn.Length..]; // e.g. ".Area()"
        foreach (var implementerType in await ClosureAsync(
            containerType.Id, [RelationshipKind.Implements, RelationshipKind.Inherits], cancellationToken).ConfigureAwait(false))
        {
            var candidates = await _store.GetNodesByFqnAsync(implementerType.Fqn + memberSuffix, cancellationToken).ConfigureAwait(false);
            foreach (var candidate in candidates.Where(c => c.Kind == member.Kind))
            {
                results[candidate.Id] = candidate;
            }
        }
    }
}
