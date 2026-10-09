namespace Fixture.Lib.Unused;

// v0.14.0 fixture for find_unused_symbols (docs/EXPANSION-SPECS.md §5): one symbol per rule.

/// <summary>Reported: nothing references the type or anything in it.</summary>
public sealed class NeverReferenced
{
    /// <summary>Reported: no caller.</summary>
    public void NeverCalled()
    {
    }
}

/// <summary>Reported: internal accessibility is searched too.</summary>
internal sealed class InternalNeverReferenced
{
}

/// <summary>
/// NOT reported: the type is never named, but its extension method is called — a type counts as
/// used when anything it contains is used.
/// </summary>
public static class UsedOnlyThroughExtension
{
    public static int Twice(this int value) => value * 2;
}

public sealed class ExtensionCaller
{
    /// <summary>Calls the extension (keeps UsedOnlyThroughExtension used); itself unreferenced — reported.</summary>
    public int Call() => 21.Twice();
}

/// <summary>
/// Lower confidence: implements an external (framework) interface — the shape of types found by
/// reflection/DI. Dispose itself is excluded as an interface implementation.
/// </summary>
public sealed class ExternalInterfaceImplementor : IDisposable
{
    public void Dispose()
    {
    }
}

public abstract class UnusedBase
{
    public abstract void Hook();
}

/// <summary>Hook is excluded as an override (reached through UnusedBase.Hook).</summary>
public sealed class UnusedDerived : UnusedBase
{
    public override void Hook()
    {
    }
}

/// <summary>
/// Reported in the MAIN list (QA finding 3): a record's compiler-synthesized IEquatable&lt;itself&gt;
/// is not a framework base type.
/// </summary>
public sealed record UnusedRecord(int Value);

public sealed class PrivateOnly
{
    // Never searched: private members are out of scope (stated in the output).
    private void Hidden()
    {
    }

    /// <summary>Uses Hidden so the class is referenced internally only — reported (nothing outside uses PrivateOnly).</summary>
    public void UsesHidden() => Hidden();
}
