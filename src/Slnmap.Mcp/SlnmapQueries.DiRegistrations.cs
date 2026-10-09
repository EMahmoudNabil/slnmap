using System.Globalization;
using System.Text;
using Slnmap.Core.Graph;
using Slnmap.Core.Storage;

namespace Slnmap.Mcp;

public sealed partial class SlnmapQueries
{
    private const int DiRegistrationsCap = 60;
    private const int DiUnrecognizedCap = 10;

    /// <summary>
    /// v0.14.0 (get_di_registrations, docs/EXPANSION-SPECS.md §8): what the DI container is wired
    /// with — service → implementation → lifetime — as written in this solution's source, grouped
    /// by project with file:line. Registrations whose types could not be read are listed
    /// separately, never dropped; library-internal registrations are stated as not expanded.
    /// </summary>
    public async Task<string> GetDiRegistrationsAsync(string? project, string? type, CancellationToken cancellationToken = default)
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
            return "This graph was built before DI registrations were recorded (schema v1) — re-run 'slnmap analyze' "
                + "with this slnmap version, then call get_di_registrations again.";
        }

        var all = await _store.GetDiRegistrationsAsync(cancellationToken).ConfigureAwait(false);
        string? projectFilter = null;
        if (!string.IsNullOrWhiteSpace(project) && !project.Trim().Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            var projects = (await _store.GetNodesByKindAsync(NodeKind.Project, cancellationToken).ConfigureAwait(false)).Select(p => p.Name).ToList();
            projectFilter = projects.FirstOrDefault(p => p.Equals(project.Trim(), StringComparison.OrdinalIgnoreCase));
            if (projectFilter is null)
            {
                return ToolFailure.InvalidParameter(
                    "project",
                    ["project", "type"],
                    $"Unknown project '{project}'. Use 'all' or one of: {string.Join(", ", projects.OrderBy(p => p, StringComparer.Ordinal))}.");
            }
        }

        string? typeFilter = string.IsNullOrWhiteSpace(type) ? null : type.Trim();
        var selected = all
            .Where(r => projectFilter is null || r.Project == projectFilter)
            .Where(r => typeFilter is null
                || r.ServiceFqn.Contains(typeFilter, StringComparison.OrdinalIgnoreCase)
                || (r.ImplementationFqn?.Contains(typeFilter, StringComparison.OrdinalIgnoreCase) ?? false))
            .ToList();
        var recognized = selected.Where(r => r.RegistrationKind != DiRegistrationKinds.Unrecognized).ToList();
        var unrecognized = selected.Where(r => r.RegistrationKind == DiRegistrationKinds.Unrecognized).ToList();

        string filterNote = (projectFilter, typeFilter) switch
        {
            (null, null) => string.Empty,
            (not null, null) => $" in {projectFilter}",
            (null, not null) => $" involving '{typeFilter}'",
            _ => $" in {projectFilter} involving '{typeFilter}'",
        };

        var builder = new StringBuilder();
        if (recognized.Count == 0 && unrecognized.Count == 0)
        {
            builder.AppendLine($"0 DI registrations{filterNote}.");
        }
        else
        {
            int projectCount = selected.Select(r => r.Project).Distinct(StringComparer.Ordinal).Count();
            bool capped = recognized.Count > DiRegistrationsCap;
            builder.AppendLine(capped
                ? $"{DiRegistrationsCap}+ DI registration(s){filterNote} across {projectCount} project(s) (showing first {DiRegistrationsCap} of {recognized.Count} — filter by project or type):"
                : $"{recognized.Count} DI registration(s){filterNote} across {projectCount} project(s):");

            var resolver = new LineResolver();
            foreach (var group in recognized.Take(DiRegistrationsCap).GroupBy(r => r.Project).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                builder.AppendLine($"{group.Key}:");
                foreach (var registration in group)
                {
                    builder.AppendLine($"  {Describe(registration)} [{registration.Lifetime}] — {registration.FilePath}:{resolver.LineOf(registration.FilePath, registration.SpanStart)}");
                }
            }

            if (unrecognized.Count > 0)
            {
                builder.AppendLine($"Unrecognized ({unrecognized.Count}) — registration calls whose types could not be read statically (e.g. a Type held in a variable):");
                foreach (var registration in unrecognized.Take(DiUnrecognizedCap))
                {
                    builder.AppendLine($"  {registration.ServiceFqn} [{registration.Lifetime}] — {registration.FilePath}:{resolver.LineOf(registration.FilePath, registration.SpanStart)}");
                }

                if (unrecognized.Count > DiUnrecognizedCap)
                {
                    builder.AppendLine($"  ...and {unrecognized.Count - DiUnrecognizedCap} more.");
                }
            }
        }

        builder.AppendLine(
            "note: only explicit Microsoft.Extensions.DependencyInjection calls in this solution's source are captured; "
            + "registrations made inside library extension methods (AddControllers, AddMediatR, Scrutor's Scan, assembly "
            + "scanning) are not expanded. For every type that could implement an interface, use find_implementations.");
        return builder.ToString().TrimEnd();
    }

    private static string Describe(DiRegistration registration)
    {
        string implementation = registration.RegistrationKind switch
        {
            DiRegistrationKinds.Factory => registration.ImplementationFqn is { } created ? $"{created} (factory)" : "(factory)",
            DiRegistrationKinds.Instance => registration.ImplementationFqn is { } instance ? $"(instance of {instance})" : "(instance)",
            _ => registration.ImplementationFqn ?? "?",
        };
        string openGeneric = registration.RegistrationKind == DiRegistrationKinds.OpenGeneric ? " (open generic)" : string.Empty;
        return registration.ServiceFqn == registration.ImplementationFqn && registration.RegistrationKind != DiRegistrationKinds.Factory
            ? $"{registration.ServiceFqn} (self){openGeneric}"
            : $"{registration.ServiceFqn} -> {implementation}{openGeneric}";
    }
}
