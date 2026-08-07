namespace Fixture.Lib;

// Exercises Gap 2: fields as graph nodes.

public sealed class FieldTypeofA
{
}

public sealed class FieldTypeofB
{
}

public static class FieldHolder
{
    // A private static readonly HashSet<Type> field, initialized with typeof(...) entries — the
    // config-carrying shape the investigation calls out. Once fields are nodes, the typeof()
    // references inside this initializer must attribute to THIS FIELD, not to FieldHolder.
    private static readonly HashSet<Type> KnownTypes = new()
    {
        typeof(FieldTypeofA),
        typeof(FieldTypeofB),
    };

    public static bool Contains(Type t) => KnownTypes.Contains(t);
}

public sealed class MultiDeclaratorFields
{
    // One FieldDeclarationSyntax, two VariableDeclaratorSyntax — must yield two Field nodes,
    // each Contains-linked to this class.
    private int _a = 1, _b = 2;

    public int Sum() => _a + _b;
}

public sealed class EventHolder
{
    // EventFieldDeclarationSyntax has the same declarator shape as a field but declares an
    // IEventSymbol, not an IFieldSymbol — deliberately NOT modeled as NodeKind.Field (or at all)
    // by this fix; NodeKind.Event exists but remains unmapped, a separate follow-up gap.
    public event EventHandler? Changed;

    public void Raise() => Changed?.Invoke(this, EventArgs.Empty);
}
