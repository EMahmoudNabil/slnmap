namespace Slnmap.Core.Graph;

/// <summary>
/// A per-file record that lives beside the node/edge graph rather than in it (schema v2). Facts
/// point at things that are NOT source symbols — an external method, a framework attribute, a DI
/// registration's type pair — so they cannot be edges (there is no node to point at). Every fact
/// is owned by the document whose walk produced it, exactly like an edge is owned by its source
/// file: incremental re-analysis evicts and regenerates a file's facts together with its nodes.
/// </summary>
public interface IFileOwnedFact
{
    /// <summary>Path of the document whose walk produced this fact.</summary>
    string FilePath { get; }

    /// <summary>Character offset of the producing syntax within <see cref="FilePath"/>.</summary>
    int SpanStart { get; }
}

/// <summary>
/// Calls (and object creations) from one source member into one symbol outside the solution:
/// one fact per (caller, target) pair, carrying the first call site and how many there are
/// (v0.14.0 storage budget, reports/v0140-gate7a-external-calls-budget.md).
/// </summary>
/// <param name="CallerId">Node id of the source member containing the call.</param>
/// <param name="TargetFqn">Fully qualified name of the external target, as text — never a node.</param>
/// <param name="TargetNamespace">Namespace of the target's containing type ("" for the global namespace).</param>
/// <param name="TargetAssembly">Name of the assembly the target is defined in (typically the package's assembly).</param>
public sealed record ExternalCall(
    string CallerId,
    string TargetFqn,
    string TargetNamespace,
    string? TargetAssembly,
    string FilePath,
    int SpanStart,
    int CallCount = 1) : IFileOwnedFact;

/// <summary>One dependency-injection registration call site.</summary>
/// <param name="ServiceFqn">The registered service type's FQN, or the call's own text when it could not be resolved.</param>
/// <param name="ImplementationFqn">The implementation type's FQN; null for factory registrations and unrecognized shapes.</param>
/// <param name="Lifetime"><c>Singleton</c>, <c>Scoped</c>, <c>Transient</c>, or <c>Unknown</c>.</param>
/// <param name="RegistrationKind">How the registration was expressed — see <see cref="DiRegistrationKinds"/>.</param>
/// <param name="CallerId">Node id of the member containing the registration call, when there is one.</param>
/// <param name="Project">Name of the project the registration lives in.</param>
public sealed record DiRegistration(
    string ServiceFqn,
    string? ImplementationFqn,
    string Lifetime,
    string RegistrationKind,
    string? CallerId,
    string FilePath,
    int SpanStart,
    string Project) : IFileOwnedFact;

/// <summary>The recognized <see cref="DiRegistration.RegistrationKind"/> values.</summary>
public static class DiRegistrationKinds
{
    public const string Generic = "generic";
    public const string TypeOf = "typeof";
    public const string Factory = "factory";
    public const string Instance = "instance";
    public const string OpenGeneric = "open-generic";
    public const string Unrecognized = "unrecognized";
}

/// <summary>An attribute applied to a source symbol.</summary>
/// <param name="TargetId">Node id of the decorated symbol (type, member, or — for parameters — the containing member).</param>
/// <param name="AttributeFqn">The attribute class's FQN, as text (usually a framework type, so never a node).</param>
public sealed record AttributeUsage(
    string TargetId,
    string AttributeFqn,
    string FilePath,
    int SpanStart) : IFileOwnedFact;

/// <summary>
/// Something analysis could not model, recorded where it was found so the disclosure survives
/// incremental re-analysis (v0.14.0). Before this, disclosure counters were summed over only the
/// documents a run re-walked and written over the stored totals, so any incremental run — every
/// <c>watch</c> save — reset them to zero.
/// </summary>
/// <param name="Kind">One of <see cref="DisclosureKinds"/>.</param>
/// <param name="Detail">What was found: a class FQN, an unresolved reason, a convention type.</param>
public sealed record Disclosure(
    string Kind,
    string Detail,
    string FilePath,
    int SpanStart) : IFileOwnedFact;

/// <summary>The recognized <see cref="Disclosure.Kind"/> values.</summary>
public static class DisclosureKinds
{
    /// <summary>An endpoint registration whose route could not be resolved statically. Detail: the reason.</summary>
    public const string UnresolvedEndpoint = "unresolved_endpoint";

    /// <summary>A conventionally-routed controller. Detail: the class FQN.</summary>
    public const string ConventionalController = "conventional_controller";

    /// <summary>A Razor Page with handlers. Detail: the class FQN.</summary>
    public const string RazorPageNotModeled = "razor_page_not_modeled";

    /// <summary>
    /// A controller whose [Route]/[Http*] attributes are written but whose attribute types don't
    /// resolve (usually: the project wasn't restored), so its routes can't be read (v0.14.1).
    /// Before, it was mislabeled conventionally routed. Detail: the class FQN.
    /// </summary>
    public const string RouteAttributesUnresolved = "route_attributes_unresolved";

    /// <summary>A class that looks like a controller but is not recognized as one. Detail: the class FQN.</summary>
    public const string ControllerLikeUnrecognized = "controller_like_unrecognized";

    /// <summary>
    /// A registered MVC application-, controller- or action-model convention, which can rewrite
    /// route templates at startup. Detail: the convention's type, plus how it was registered.
    /// </summary>
    public const string RouteConvention = "route_convention";

    /// <summary>
    /// A controller endpoint whose template includes the prefix the user supplied with
    /// <c>slnmap analyze --route-prefix</c> — stated by the user, not derived from code. Detail:
    /// the endpoint's FQN. Not a counter; it marks each affected endpoint in tool output.
    /// </summary>
    public const string RoutePrefixApplied = "route_prefix_applied";

    /// <summary>
    /// Which path segments of a controller endpoint's template were substituted from a
    /// [controller]/[action]/[area] token — the only segments an MVC
    /// <c>RouteTokenTransformerConvention</c> rewrites (e.g. slugifies). Provenance, not a
    /// counter. Detail: <c>"{endpoint FQN}\t{comma-separated zero-based indexes}"</c>, indexes
    /// into the normalized template's segments. See <see cref="TokenSegmentsDetail"/>.
    /// </summary>
    public const string TokenSegments = "token_segments";

    /// <summary>
    /// A project analyzed without its dependencies: it has no resolved metadata references at all
    /// (restore never ran, or failed), or its restore recorded errors. Every framework and package
    /// type in it is unresolved, so its endpoints, DI registrations, attribute usages, external
    /// calls and references are incomplete. Owned by the project file, recomputed on every run.
    /// Detail: <c>"{project name}\t{reason}"</c>. See <see cref="ProjectNotRestoredDetail"/>.
    /// </summary>
    public const string ProjectNotRestored = "project_not_restored";

    /// <summary>Formats a <see cref="ProjectNotRestored"/> detail.</summary>
    public static string ProjectNotRestoredDetail(string projectName, string reason) =>
        $"{projectName}\t{reason}";

    /// <summary>The project name of a <see cref="ProjectNotRestored"/> detail.</summary>
    public static string ProjectNotRestoredName(string detail)
    {
        int tab = detail.IndexOf('\t', StringComparison.Ordinal);
        return tab < 0 ? detail : detail[..tab];
    }

    /// <summary>The reason of a <see cref="ProjectNotRestored"/> detail.</summary>
    public static string ProjectNotRestoredReason(string detail)
    {
        int tab = detail.IndexOf('	', StringComparison.Ordinal);
        return tab < 0 ? string.Empty : detail[(tab + 1)..];
    }

    /// <summary>Formats a <see cref="TokenSegments"/> detail.</summary>
    public static string TokenSegmentsDetail(string endpointFqn, IEnumerable<int> indexes) =>
        $"{endpointFqn}\t{string.Join(',', indexes)}";

    /// <summary>Parses a <see cref="TokenSegments"/> detail; false when it is malformed.</summary>
    public static bool TryParseTokenSegmentsDetail(string detail, out string endpointFqn, out IReadOnlySet<int> indexes)
    {
        endpointFqn = string.Empty;
        indexes = new HashSet<int>();
        int tab = detail.LastIndexOf('\t');
        if (tab <= 0)
        {
            return false;
        }

        var parsed = new HashSet<int>();
        foreach (string part in detail[(tab + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(part, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int index))
            {
                return false;
            }

            parsed.Add(index);
        }

        endpointFqn = detail[..tab];
        indexes = parsed;
        return true;
    }
}
