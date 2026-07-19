namespace Slnmap.Core.Graph;

/// <summary>
/// The kind of relationship a <see cref="RelationshipEdge"/> represents.
/// Values are persisted as integers; never reorder or renumber existing members.
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
}
