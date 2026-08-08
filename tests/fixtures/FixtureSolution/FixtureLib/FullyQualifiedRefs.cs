// Fixture for issue #4: fully-qualified (no `using` shortcut) type references still produce NO
// References edge, because the reference's SimpleNameSyntax is the rightmost segment of a
// QualifiedNameSyntax, and QualifiedNameSyntax is unconditionally excluded by
// DocumentWalker.IsNonExpressionContext. See reports/issue-4-investigation.md.
//
// This file is deliberately separate from TypeReferences.cs's existing
// FullyQualifiedRefTarget/FullyQualifiedRefUser pair (which documents the parameter-type case as
// a "known residual gap" and asserts the CURRENT, buggy behavior) — these fixtures assert the
// OPPOSITE (the edge SHOULD exist) for three cases the existing fixture doesn't cover: typeof,
// parameter type (a second, independent instance), and a generic type argument. They are expected
// to FAIL against current main; see FullyQualifiedReferenceGapTests.cs.

using Fixture.Lib.FullyQualifiedGap;
using static Fixture.Lib.FullyQualifiedGap.GapStaticHost;

// The namespace declaration's own multi-segment Name ("Fixture.Lib.FullyQualifiedGap") has the
// exact same syntax shape as the type references below (a QualifiedNameSyntax whose rightmost
// SimpleNameSyntax is "FullyQualifiedGap") — it must never produce a References edge. See
// NamespaceDeclaration_FullyQualifiedName_NeverProducesReferenceEdge.
namespace Fixture.Lib.FullyQualifiedGap
{
    public sealed class GapTypeofTarget
    {
    }

    public sealed class GapParameterTarget
    {
    }

    public sealed class GapGenericArgTarget
    {
    }

    // A using-static target: unlike the plain `using Fixture.Lib.FullyQualifiedGap;` above (which
    // imports a namespace, resolving its leaf to an INamespaceSymbol that HandleNameReference's own
    // symbol-kind filter already excludes regardless of IsNonExpressionContext), this leaf resolves
    // to an INamedTypeSymbol — a real over-fix risk a naive "just stop excluding QualifiedNameSyntax
    // leaves" change would trip on. See
    // UsingStaticDirective_FullyQualifiedTypeImport_NeverProducesReferenceEdge.
    public static class GapStaticHost
    {
        public static void DoNothing()
        {
        }
    }

    // Integration-added (v0.6.0, reports/issue-4-investigation.md §4's flagged interaction risk):
    // a CRTP self-reference via a FULLY-QUALIFIED generic base-list argument. Exercises the same
    // self-loop guard as TypeReferences.cs's SelfRef (HandleNameReference's `source.Id != target.Id`
    // check), but through the newly-included qualified leaf — the one place the fix and v0.5.0's
    // self-loop special-case are checked at the same call site. See
    // Integration_FullyQualifiedSelfReference_DoesNotCreateSelfLoopReferenceEdge.
    public sealed class GapSelfRef : System.IEquatable<Fixture.Lib.FullyQualifiedGap.GapSelfRef>
    {
        public bool Equals(GapSelfRef? other) => ReferenceEquals(this, other);

        public override bool Equals(object? obj) => Equals(obj as GapSelfRef);

        public override int GetHashCode() => 0;
    }
}

namespace Fixture.Lib
{
    public static class FullyQualifiedGapUser
    {
        // Issue #4, case 1: fully-qualified typeof(). GapTypeofTarget is referenced nowhere else,
        // so a correct fix must be the only thing keeping it reachable from here.
        public static System.Type TypeofCase() => typeof(Fixture.Lib.FullyQualifiedGap.GapTypeofTarget);

        // Issue #4, case 2: fully-qualified parameter type.
        public static void ParameterCase(Fixture.Lib.FullyQualifiedGap.GapParameterTarget value) => _ = value;

        // Issue #4, case 3: fully-qualified generic type argument.
        public static System.Collections.Generic.List<Fixture.Lib.FullyQualifiedGap.GapGenericArgTarget> GenericArgCase() => new();

        // Exercises the `using` (namespace import, via the short-named local below) and
        // `using static` (type import, via the unqualified DoNothing() call) directives above —
        // both stay genuinely live, not dead code that could be deleted without changing this
        // fixture's meaning. This method's own references are all short-named and already work
        // correctly today; it exists only to justify the using directives, not as part of the gap.
        public static void UseImports()
        {
            DoNothing();
            GapTypeofTarget shortNamed = new();
            _ = shortNamed;
        }
    }
}
