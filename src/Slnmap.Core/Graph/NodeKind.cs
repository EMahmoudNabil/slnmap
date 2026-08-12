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
}
