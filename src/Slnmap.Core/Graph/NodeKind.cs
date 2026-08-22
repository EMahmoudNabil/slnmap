namespace Slnmap.Core.Graph;

/// <summary>
/// The kind of symbol a <see cref="SymbolNode"/> represents.
/// Persisted in the database as the enum member's name (TEXT, not its integer value) —
/// never rename an existing member without a migration.
/// </summary>
public enum NodeKind
{
    Unknown = 0,
    Project = 1,
    Namespace = 2,
    Class = 3,
    Interface = 4,
    Struct = 5,
    Record = 6,
    Enum = 7,
    Delegate = 8,
    Method = 9,
    Constructor = 10,
    Property = 11,
    Field = 12,
    Event = 13,

    /// <summary>
    /// An HTTP endpoint registration (ASP.NET Core Minimal API): fqn = "VERB template",
    /// name = the composed route template. Not a Roslyn symbol — synthesized from the
    /// Map* call site. Append-only: viz indexes kinds positionally, never insert mid-enum.
    /// </summary>
    Endpoint = 14,

    /// <summary>
    /// An enum member (enumerant). Roslyn models these as IFieldSymbol, but they are values of
    /// a closed set, not mutable state — a distinct kind keeps Field censuses honest (#13).
    /// </summary>
    EnumMember = 15,

    /// <summary>
    /// A resolved frontend HTTP call site (the `slnmap-ts` extractor, `analyze-ts` verb):
    /// fqn = "VERB relativeFile:line:column", name = the resolved route template. Not a Roslyn
    /// symbol — one node per call SITE, unlike <see cref="Endpoint"/>'s dedup-by-route identity,
    /// because two call sites hitting the same route are two distinct, independently useful
    /// facts (reports/ts-extractor-investigation.md §Q2.2). Zero edges in this phase — the
    /// cross-stack linker is Phase 3.
    /// </summary>
    FrontendCallSite = 16,

    /// <summary>
    /// A frontend HTTP call site the extractor could not resolve statically: fqn =
    /// "VERB-or-UNKNOWN category relativeFile:line:column", name = "category: reason". A first-
    /// class node kind (not a warning, unlike unresolved Endpoint registrations) specifically so
    /// coverage is queryable via the existing nodes-by-kind census, per the six closed categories
    /// in ts-extractor-investigation.md §Q3.3.
    /// </summary>
    UnresolvedCallSite = 17,
}
