namespace Fixture.Lib;

// Exercises LIE 1 (v0.6.1): fields/consts must have INCOMING References edges, not just nodes
// and outgoing edges. Mirrors a production field-eval shape: a Constants-style class whose const
// members are read across multiple files and projects (SellerActivityType.Deactivated).

public static class SellerActivityTypes
{
    // Read in three files: DeactivateSellerCommand.cs (this project), SellerAudit.cs
    // (FixtureApp), and below in this file — the eval's "const used in N files" case.
    public const string Deactivated = "seller.deactivated";

    // Referenced ONLY from another field's initializer (_label below) — pins the
    // field-initializer-as-source attribution.
    public const string Activated = "seller.activated";

    // Referenced nowhere — must report no usages, and must never self-report its own declaration.
    public const string Unused = "seller.unused";
}

public enum SellerState
{
    // Never referenced anywhere — the census-consistency case that kept enum members unmodeled
    // until v0.9.0: EVERY member must materialize via the declaration walk, referenced or not.
    Active,
    // Referenced from SellerStateReader.Current below: usage reaches the MEMBER node (#13) and,
    // via the "SellerState" segment, the enum-type node — both.
    Deactivated,
}

public sealed class FieldUsageHolder
{
    // Read AND written intra-class through several expression shapes (compound, plain
    // assignment, argument position, string interpolation).
    private int _counter;

    // Initializer only, never referenced anywhere — must have zero usages.
    private readonly int _initializerOnly = 5;

    // Referenced ONLY via nameof — pins the nameof-is-a-usage decision.
    private int _named;

    // The initializer reads a const: the References edge must attribute to THIS FIELD as the
    // source (post-Gap-2 GetEnclosingMemberNode behavior), targeting the const's node.
    private string _label = SellerActivityTypes.Activated;

    // A static field legally reading itself in its own initializer (evaluates to default) —
    // the source and target resolve to the same node, and the existing self-loop guard in
    // HandleNameReference must suppress the edge.
    private static string? _selfRef = _selfRef;

    public void Increment() => _counter++;

    public void Reset() => _counter = 0;

    public int Magnitude() => Math.Abs(_counter);

    public string Describe() => $"count={_counter}, label={_label}, self={_selfRef}";

    public string NamedFieldName() => nameof(_named);

    // nameof in an attribute argument sits in the method's SIGNATURE position — #4's
    // signature-position attribution must credit the method itself as the source.
    [Obsolete(nameof(_named))]
    public void Legacy()
    {
    }

    public bool IsDeactivation(string type) => type == SellerActivityTypes.Deactivated;
}

// Enum-member consumer: SellerState.Deactivated produces References edges to BOTH the member
// node (#13, v0.9.0) and the enum type (via the "SellerState" segment).
public sealed class SellerStateReader
{
    public SellerState Current { get; } = SellerState.Deactivated;
}
