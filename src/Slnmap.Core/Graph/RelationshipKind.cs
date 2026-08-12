namespace Slnmap.Core.Graph;

/// <summary>
/// The kind of relationship a <see cref="RelationshipEdge"/> represents.
/// Persisted in the database as the enum member's name (TEXT, not its integer value) —
/// never rename an existing member without a migration, and only append new members
/// (viz indexes edge kinds positionally).
/// </summary>
public enum RelationshipKind
{
    /// <summary>Source invokes target (method/constructor invocation).</summary>
    Calls = 0,

    /// <summary>Source implements target interface.</summary>
    Implements = 1,

    /// <summary>Source derives from target class.</summary>
    Inherits = 2,

    /// <summary>Source mentions target (type usage, member access) without a more specific relationship.</summary>
    References = 3,

    /// <summary>Source lexically contains target (project → namespace → type → member).</summary>
    Contains = 4,

    /// <summary>Source endpoint is handled by target method (Endpoint —HandledBy→ Method).</summary>
    HandledBy = 5,

    /// <summary>
    /// Never persisted by this version: the graceful fallback a reader maps an edge kind
    /// written by a newer slnmap onto, instead of crashing (see SqliteGraphStore.ParseEnum).
    /// </summary>
    Unknown = 6,
}
