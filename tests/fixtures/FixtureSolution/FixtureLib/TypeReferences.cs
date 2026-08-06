using CircleAlias = Fixture.Lib.Circle;

namespace Fixture.Lib;

// Exercises Gap 1: type references reachable ONLY through a generic type argument, typeof(), or
// an attribute constructor argument — never through a direct call, object creation, or
// declaration. Each referenced-only type below is intentionally never named anywhere outside its
// one reference site, so find_usages/impact_analysis on it is a direct test of this fix.

public static class Registrar
{
    public static void Register<T>() where T : class
    {
    }
}

// Referenced ONLY via Registrar.Register<GenericMethodArgOnly>() below.
public sealed class GenericMethodArgOnly
{
}

// Referenced ONLY via new List<GenericCreationArgOnly>() below.
public sealed class GenericCreationArgOnly
{
}

// Referenced ONLY via typeof(TypeofOnly) below.
public sealed class TypeofOnly
{
}

[AttributeUsage(AttributeTargets.Class)]
public sealed class MarkerAttribute : Attribute
{
    public MarkerAttribute(Type target) => Target = target;

    public Type Target { get; }
}

// Referenced ONLY as the attribute constructor argument below (typeof(AttributeArgOnly)).
public sealed class AttributeArgOnly
{
}

[Marker(typeof(AttributeArgOnly))]
public sealed class Marked
{
}

public static class GenericRefs
{
    public static void UseAll()
    {
        Registrar.Register<GenericMethodArgOnly>();
        var list = new List<GenericCreationArgOnly>();
        _ = list.Count;
        _ = typeof(TypeofOnly);
    }
}

// Recursive-generic self-reference (CRTP): SelfRef mentions its own type inside a generic type
// argument in its own base list. Must not create a Foo -> Foo self-loop Reference edge.
public sealed class SelfRef : IEquatable<SelfRef>
{
    public bool Equals(SelfRef? other) => ReferenceEquals(this, other);

    public override bool Equals(object? obj) => Equals(obj as SelfRef);

    public override int GetHashCode() => 0;
}

// A member referencing its own containing type by name (a local variable's type annotation, not
// via `new`) is a real, wanted reference, not noise.
public sealed class CopyTarget
{
    public static CopyTarget Clone(CopyTarget source)
    {
        CopyTarget clone = source;
        return clone;
    }
}

public interface INameHolder
{
    string Name { get; }
}

// Exercises ExplicitInterfaceSpecifierSyntax: the "INameHolder" before ".Name" must not create an
// extra References edge duplicating the type-level Implements edge already produced elsewhere.
public sealed class ExplicitNameHolder : INameHolder
{
    string INameHolder.Name => "explicit";
}

public static class NamedArgUser
{
    public static string Format(int value) => value.ToString();

    // Exercises NameColonSyntax: the "value:" label must not itself resolve to anything.
    public static void UseNamedArg() => _ = Format(value: 5);

    // Exercises the `using CircleAlias = ...;` directive above: the alias's own Name/Alias syntax
    // (NameEqualsSyntax + QualifiedNameSyntax) must not create a References edge by itself — only
    // an actual use of the alias should (and even that resolves through the qualified name, so it
    // stays excluded too; this method exists to prove the alias declaration compiles and is inert).
    public static CircleAlias? MakeAliasedCircle() => null;
}

public sealed class FullyQualifiedRefTarget
{
}

public static class FullyQualifiedRefUser
{
    // KNOWN RESIDUAL GAP (out of scope for this fix, documented in
    // reports/gap-fix-implementation.md): a fully-qualified (no `using` shortcut) type reference
    // is still excluded, because "FullyQualifiedRefTarget" here is the rightmost SimpleNameSyntax
    // of a QualifiedNameSyntax ("Fixture.Lib.FullyQualifiedRefTarget"), and QualifiedNameSyntax is
    // (deliberately) still excluded by IsNonExpressionContext for using-directive scaffolding.
    public static void Accept(Fixture.Lib.FullyQualifiedRefTarget value) => _ = value;
}
