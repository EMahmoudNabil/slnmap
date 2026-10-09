using Slnmap.Core.Graph;

namespace Slnmap.Mcp;

public sealed partial class SlnmapQueries
{
    /// <summary>Project names listed in the not-restored note before it summarizes the rest.</summary>
    internal const int NotRestoredNamesCap = 5;

    /// <summary>
    /// The one-line warning prefixed to every tool's answer when the graph was built from projects
    /// analyzed without their dependencies (v0.14.0, <see cref="DisclosureKinds.ProjectNotRestored"/>),
    /// or null when every project resolved. It qualifies every answer — a missing framework type
    /// changes what any tool can see — so it is attached at the tool choke point, not per tool.
    /// </summary>
    public async Task<string?> NotRestoredNoteAsync(CancellationToken cancellationToken = default)
    {
        var disclosures = await _store.GetDisclosuresAsync(DisclosureKinds.ProjectNotRestored, cancellationToken).ConfigureAwait(false);
        var names = disclosures
            .Select(static d => DisclosureKinds.ProjectNotRestoredName(d.Detail))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        if (names.Count == 0)
        {
            return null;
        }

        string listed = string.Join(", ", names.Take(NotRestoredNamesCap));
        if (names.Count > NotRestoredNamesCap)
        {
            listed += $" (+{names.Count - NotRestoredNamesCap} more)";
        }

        return $"Warning: incomplete graph. {names.Count} project(s) were analyzed without their dependencies "
            + $"(not restored, or the restore failed): {listed}. In those projects, framework and package types did "
            + "not resolve, so endpoints, DI registrations, attribute usages, external calls and references are "
            + "missing or wrong. Run 'dotnet restore', then 'slnmap analyze'.";
    }
}
