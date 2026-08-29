namespace Slnmap.Core.Storage;

/// <summary>Well-known keys stored in the graph's <c>meta</c> table.</summary>
public static class MetaKeys
{
    /// <summary>Integer schema version the database was written with.</summary>
    public const string SchemaVersion = "schema_version";

    /// <summary>Round-trip ("O") timestamp of the last completed analysis.</summary>
    public const string LastAnalyzed = "last_analyzed";

    /// <summary>Absolute path of the solution or project the graph was built from.</summary>
    public const string SolutionPath = "solution_path";

    /// <summary>
    /// Version of the slnmap tool that produced this graph (the CLI assembly's version, e.g.
    /// <c>"0.5.0"</c>). Absent in databases written before this key existed.
    /// </summary>
    public const string ToolVersion = "tool_version";

    /// <summary>
    /// Count of endpoint registrations the last analysis could not resolve statically (counted,
    /// never guessed — see the endpoint-nodes design). Absent or "0" when everything resolved.
    /// </summary>
    public const string UnresolvedEndpoints = "unresolved_endpoints";

    /// <summary>
    /// Count of conventionally-routed controllers (no route attributes) the last analysis
    /// detected — a different routing system, disclosed so "0 endpoints" on an MVC codebase is
    /// never a silent mystery. Absent or "0" when none exist.
    /// </summary>
    public const string ConventionalControllers = "conventional_controllers";

    /// <summary>
    /// Count of Razor Pages (PageModel-derived classes with OnGet/OnPost/... handlers) the last
    /// analysis detected — route by file location, a different routing system, disclosed for the
    /// same reason as <see cref="ConventionalControllers"/> (v0.12.2). Absent or "0" when none exist.
    /// </summary>
    public const string RazorPagesNotModeled = "razor_pages_not_modeled";

    /// <summary>
    /// Count of <c>.razor</c> files the last analysis found on disk under an analyzed project's
    /// directory — Blazor component markup is not walked as an analyzer document at all, so this
    /// is a file-system count, not a graph one (v0.12.2). Absent or "0" when none exist.
    /// </summary>
    public const string RazorFilesDetected = "razor_files_detected";

    /// <summary>
    /// Count of classes that look like an MVC controller syntactically but were not recognized as
    /// one semantically — the last analysis disclosed each with a reason rather than silently
    /// skipping it (v0.13.1). Absent or "0" when none exist.
    /// </summary>
    public const string ControllerLikeClassesUnrecognized = "controller_like_classes_unrecognized";

    /// <summary>
    /// Round-trip ("O") timestamp of the last completed `analyze-ts` run. Compared against
    /// <see cref="LastAnalyzed"/> to surface the ts-extractor-investigation.md §Q1.2 ordering
    /// note: `analyze` rebuilds the graph from Roslyn output alone and does not know about
    /// frontend-producer node kinds, so it silently drops them on its next full rebuild — a
    /// newer <see cref="LastAnalyzed"/> than <see cref="FrontendLastAnalyzed"/> means the
    /// frontend data `analyze-ts` is about to write is restoring exactly what that rebuild
    /// dropped. Absent in a database that has never run `analyze-ts`.
    /// </summary>
    public const string FrontendLastAnalyzed = "frontend_last_analyzed";

    /// <summary>
    /// Count of frontend call sites the last `analyze-ts` run could not resolve statically
    /// (counted, never guessed — the six closed categories in
    /// ts-extractor-investigation.md §Q3.3). Absent or "0" when everything resolved, or when
    /// `analyze-ts` has never run.
    /// </summary>
    public const string FrontendUnresolvedCallSites = "frontend_unresolved_call_sites";

    /// <summary>
    /// Round-trip ("O") timestamp of the last completed `slnmap link` run (the cross-stack
    /// linker, cross-stack-linker-investigation.md §Q3/§Q6). Neither <c>analyze</c> nor
    /// <c>analyze-ts</c> clears this key — both preserve whatever meta they don't own — but
    /// either one changing the graph makes the last `link` run's <c>CallsEndpoint</c> edges
    /// stale; each prints a one-line note when this key is present, per the same staleness
    /// pattern <see cref="FrontendLastAnalyzed"/> already established. Absent when `link` has
    /// never run.
    /// </summary>
    public const string LinkerLastRun = "linker_last_run";

    /// <summary>
    /// The base-path prefix the last `slnmap link` run was given (`--base-path`, v0.12.3;
    /// defaults to <c>"/api"</c> when the flag isn't passed). Persisted so the live-recomputing
    /// MCP query tools (`find_orphan_calls`, `list_frontend_callsites`) match what `link`
    /// actually used rather than silently reverting to the hardcoded default — see
    /// cross-stack-linker-implementation.md Part 3's "never stale relative to the current node
    /// set" guarantee, which would otherwise quietly stop being true for a non-default prefix.
    /// Absent when `link` has never run (or predates this key).
    /// </summary>
    public const string LinkerBasePathPrefix = "linker_base_path_prefix";
}
