namespace Slnmap.Core.Graph;

/// <summary>
/// The kind of symbol a <see cref="SymbolNode"/> represents.
/// Values are persisted as integers; never reorder or renumber existing members.
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
}
