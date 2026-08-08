using Slnmap.Core.Graph;
using Xunit;

namespace Slnmap.Tests;

/// <summary>
/// Issue #4: a fully-qualified (no `using` shortcut) type reference produces no References edge,
/// because its SimpleNameSyntax is the rightmost segment of a QualifiedNameSyntax, and
/// QualifiedNameSyntax is unconditionally excluded by DocumentWalker.IsNonExpressionContext. See
/// reports/issue-4-investigation.md and tests/fixtures/FixtureSolution/FixtureLib/FullyQualifiedRefs.cs.
///
/// The first three tests below are EXPECTED TO FAIL against current main — they assert the edge
/// SHOULD exist, capturing the gap (Step 3 of the investigation). The remaining two are guard
/// tests that must currently PASS and must keep passing once the fix in DocumentWalker.cs lands
/// (narrowing the exclusion without reopening it for using-directives/namespace declarations).
/// This file adds NO analyzer changes — it is investigation-only, per the task's hard rule.
/// </summary>
public sealed class FullyQualifiedReferenceGapTests : IClassFixture<AnalyzedFixtureSolution>
{
    private readonly AnalyzedFixtureSolution _fixture;

    public FullyQualifiedReferenceGapTests(AnalyzedFixtureSolution fixture) => _fixture = fixture;

    private CodeGraph Graph => _fixture.Graph;

    // --- Expected to FAIL against current main (the gap) ---

    [Fact]
    public void Gap_FullyQualifiedTypeofArgument_ShouldCreateReferenceEdge()
    {
        var source = GraphAssert.Node(Graph, NodeKind.Method, "Fixture.Lib.FullyQualifiedGapUser.TypeofCase()");
        var target = GraphAssert.Node(Graph, NodeKind.Class, "Fixture.Lib.FullyQualifiedGap.GapTypeofTarget");
        GraphAssert.Edge(Graph, source, target, RelationshipKind.References);
    }

    [Fact]
    public void Gap_FullyQualifiedParameterType_ShouldCreateReferenceEdge()
    {
        var source = GraphAssert.Node(Graph, NodeKind.Method, "Fixture.Lib.FullyQualifiedGapUser.ParameterCase(Fixture.Lib.FullyQualifiedGap.GapParameterTarget)");
        var target = GraphAssert.Node(Graph, NodeKind.Class, "Fixture.Lib.FullyQualifiedGap.GapParameterTarget");
        GraphAssert.Edge(Graph, source, target, RelationshipKind.References);
    }

    [Fact]
    public void Gap_FullyQualifiedGenericTypeArgument_ShouldCreateReferenceEdge()
    {
        var source = GraphAssert.Node(Graph, NodeKind.Method, "Fixture.Lib.FullyQualifiedGapUser.GenericArgCase()");
        var target = GraphAssert.Node(Graph, NodeKind.Class, "Fixture.Lib.FullyQualifiedGap.GapGenericArgTarget");
        GraphAssert.Edge(Graph, source, target, RelationshipKind.References);
    }

    // --- Guard tests: must PASS today, and must keep passing once the gap above is fixed ---

    [Fact]
    public void Guard_UsingDirective_FullyQualifiedNamespaceImport_NeverProducesReferenceEdge()
    {
        // The plain `using Fixture.Lib.FullyQualifiedGap;` directive's leaf ("FullyQualifiedGap")
        // resolves to an INamespaceSymbol. HandleNameReference's own symbol-kind filter
        // (`symbol is not (IPropertySymbol or IMethodSymbol or INamedTypeSymbol)`) already excludes
        // namespace symbols regardless of IsNonExpressionContext's verdict, so this guard is
        // structurally safe even against a naive fix — included for completeness/documentation,
        // not because it is the risky case (see the using-static guard below for that).
        var target = GraphAssert.Node(Graph, NodeKind.Namespace, "Fixture.Lib.FullyQualifiedGap");
        Assert.Empty(Graph.IncomingEdges(target.Id, RelationshipKind.References));
    }

    [Fact]
    public void Guard_UsingStaticDirective_FullyQualifiedTypeImport_NeverProducesReferenceEdge()
    {
        // Unlike the plain `using` above, `using static Fixture.Lib.FullyQualifiedGap.GapStaticHost;`
        // resolves its leaf ("GapStaticHost") to an INamedTypeSymbol — the SAME symbol kind a
        // typeof()/parameter/generic-argument reference resolves to. This is the guard that a naive
        // fix (e.g. deleting the `QualifiedNameSyntax => true` arm outright, instead of narrowing it
        // to the true chain-terminating leaf and excluding UsingDirectiveSyntax specifically) would
        // actually fail: it would start creating a spurious References edge here.
        var target = GraphAssert.Node(Graph, NodeKind.Class, "Fixture.Lib.FullyQualifiedGap.GapStaticHost");
        Assert.Empty(Graph.IncomingEdges(target.Id, RelationshipKind.References));
    }

    [Fact]
    public void Guard_NamespaceDeclaration_FullyQualifiedName_NeverProducesReferenceEdge()
    {
        // "namespace Fixture.Lib.FullyQualifiedGap { ... }": the declaration's own Name has the
        // exact same syntax shape (a QualifiedNameSyntax whose rightmost SimpleNameSyntax is
        // "FullyQualifiedGap") as the type references above. Its leaf resolves to the same
        // INamespaceSymbol as the plain `using` case, so — like that guard — this is safe today
        // for the same structural reason, not because IsNonExpressionContext's namespace-decl arm
        // is load-bearing on its own; kept as an explicit, separately named regression guard so a
        // future reader doesn't have to re-derive that reasoning.
        var target = GraphAssert.Node(Graph, NodeKind.Namespace, "Fixture.Lib.FullyQualifiedGap");
        Assert.Empty(Graph.IncomingEdges(target.Id, RelationshipKind.References));
    }

    // --- Integration-added (v0.6.0): #4's flagged interaction risk with v0.5.0's CRTP self-loop guard ---

    [Fact]
    public void Integration_FullyQualifiedSelfReference_DoesNotCreateSelfLoopReferenceEdge()
    {
        // GapSelfRef : System.IEquatable<Fixture.Lib.FullyQualifiedGap.GapSelfRef> — the same CRTP
        // shape as TypeReferences.cs's SelfRef, but the generic argument is fully-qualified, so it
        // now flows through the newly-included qualified leaf (issue-4-investigation.md §4). The
        // self-loop guard in HandleNameReference (`source.Id != target.Id`) must still suppress it.
        var node = GraphAssert.Node(Graph, NodeKind.Class, "Fixture.Lib.FullyQualifiedGap.GapSelfRef");
        Assert.DoesNotContain(
            Graph.Edges,
            e => e.SourceId == node.Id && e.TargetId == node.Id && e.Kind == RelationshipKind.References);
    }
}
