using Slnmap.Analysis;
using Slnmap.Core.Analysis;
using Slnmap.Core.Graph;
using Xunit;

namespace Slnmap.Tests;

/// <summary>Restores and analyzes the fixture solution once; shared by all full-analysis tests.</summary>
public sealed class AnalyzedFixtureSolution : IAsyncLifetime
{
    public AnalysisSnapshot Snapshot { get; private set; } = null!;

    public CodeGraph Graph => Snapshot.Graph;

    public async Task InitializeAsync()
    {
        DotNet.Run($"restore \"{TestPaths.FixtureSolution}\"", TestPaths.RepoRoot);
        Snapshot = await new RoslynSolutionAnalyzer().AnalyzeAsync(TestPaths.FixtureSolution);
    }

    public Task DisposeAsync() => Task.CompletedTask;
}

public sealed class FullAnalysisTests : IClassFixture<AnalyzedFixtureSolution>
{
    private readonly AnalyzedFixtureSolution _fixture;

    public FullAnalysisTests(AnalyzedFixtureSolution fixture) => _fixture = fixture;

    private CodeGraph Graph => _fixture.Graph;

    [Fact]
    public void ExtractsTypeNodes()
    {
        GraphAssert.Node(Graph, NodeKind.Interface, "Fixture.Lib.IShape");
        GraphAssert.Node(Graph, NodeKind.Class, "Fixture.Lib.ShapeBase");
        GraphAssert.Node(Graph, NodeKind.Class, "Fixture.Lib.Circle");
        GraphAssert.Node(Graph, NodeKind.Class, "Fixture.Lib.Square");
        GraphAssert.Node(Graph, NodeKind.Class, "Fixture.Lib.Geometry");
    }

    [Fact]
    public void ExtractsMemberNodes()
    {
        GraphAssert.Node(Graph, NodeKind.Method, "Fixture.Lib.IShape.Area()");
        GraphAssert.Node(Graph, NodeKind.Method, "Fixture.Lib.Circle.Area()");
        GraphAssert.Node(Graph, NodeKind.Method, "Fixture.Lib.ShapeBase.Describe()");
        GraphAssert.Node(Graph, NodeKind.Property, "Fixture.Lib.Circle.Radius");
        GraphAssert.Node(
            Graph,
            NodeKind.Method,
            "Fixture.Lib.Geometry.TotalArea(System.Collections.Generic.IEnumerable<Fixture.Lib.IShape>)");
    }

    [Fact]
    public void ExtractsImplementsAndInheritsEdges()
    {
        var shapeBase = GraphAssert.Node(Graph, NodeKind.Class, "Fixture.Lib.ShapeBase");
        var shapeContract = GraphAssert.Node(Graph, NodeKind.Interface, "Fixture.Lib.IShape");
        var circle = GraphAssert.Node(Graph, NodeKind.Class, "Fixture.Lib.Circle");
        var square = GraphAssert.Node(Graph, NodeKind.Class, "Fixture.Lib.Square");

        GraphAssert.Edge(Graph, shapeBase, shapeContract, RelationshipKind.Implements);
        GraphAssert.Edge(Graph, circle, shapeBase, RelationshipKind.Inherits);
        GraphAssert.Edge(Graph, square, shapeBase, RelationshipKind.Inherits);
    }

    [Fact]
    public void ExtractsCallEdges()
    {
        var totalArea = GraphAssert.Node(
            Graph,
            NodeKind.Method,
            "Fixture.Lib.Geometry.TotalArea(System.Collections.Generic.IEnumerable<Fixture.Lib.IShape>)");
        var interfaceArea = GraphAssert.Node(Graph, NodeKind.Method, "Fixture.Lib.IShape.Area()");
        var describe = GraphAssert.Node(Graph, NodeKind.Method, "Fixture.Lib.ShapeBase.Describe()");
        var baseArea = GraphAssert.Node(Graph, NodeKind.Method, "Fixture.Lib.ShapeBase.Area()");

        GraphAssert.Edge(Graph, totalArea, interfaceArea, RelationshipKind.Calls);
        GraphAssert.Edge(Graph, describe, baseArea, RelationshipKind.Calls);
    }

    [Fact]
    public void ExtractsCrossProjectCallFromEntryPoint()
    {
        var totalArea = GraphAssert.Node(
            Graph,
            NodeKind.Method,
            "Fixture.Lib.Geometry.TotalArea(System.Collections.Generic.IEnumerable<Fixture.Lib.IShape>)");

        var callers = Graph.IncomingEdges(totalArea.Id, RelationshipKind.Calls)
            .Select(e => Graph.TryGetNode(e.SourceId, out var n) ? n : null)
            .Where(n => n is not null)
            .ToList();

        Assert.Contains(callers, caller => caller!.FilePath?.EndsWith("Program.cs", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void ExtractsReferenceEdges()
    {
        var circleArea = GraphAssert.Node(Graph, NodeKind.Method, "Fixture.Lib.Circle.Area()");
        var radius = GraphAssert.Node(Graph, NodeKind.Property, "Fixture.Lib.Circle.Radius");
        GraphAssert.Edge(Graph, circleArea, radius, RelationshipKind.References);

        // `new Circle(...)` in the entry point becomes a References edge to the type.
        var circle = GraphAssert.Node(Graph, NodeKind.Class, "Fixture.Lib.Circle");
        var referrers = Graph.IncomingEdges(circle.Id, RelationshipKind.References)
            .Select(e => Graph.TryGetNode(e.SourceId, out var n) ? n : null)
            .ToList();
        Assert.Contains(referrers, r => r?.FilePath?.EndsWith("Program.cs", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void ExtractsContainmentChain()
    {
        var project = GraphAssert.Node(Graph, NodeKind.Project, "FixtureLib");
        var fixtureNs = GraphAssert.Node(Graph, NodeKind.Namespace, "Fixture");
        var libNs = GraphAssert.Node(Graph, NodeKind.Namespace, "Fixture.Lib");
        var circle = GraphAssert.Node(Graph, NodeKind.Class, "Fixture.Lib.Circle");
        var circleArea = GraphAssert.Node(Graph, NodeKind.Method, "Fixture.Lib.Circle.Area()");
        var radius = GraphAssert.Node(Graph, NodeKind.Property, "Fixture.Lib.Circle.Radius");

        GraphAssert.Edge(Graph, project, fixtureNs, RelationshipKind.Contains);
        GraphAssert.Edge(Graph, fixtureNs, libNs, RelationshipKind.Contains);
        GraphAssert.Edge(Graph, libNs, circle, RelationshipKind.Contains);
        GraphAssert.Edge(Graph, circle, circleArea, RelationshipKind.Contains);
        GraphAssert.Edge(Graph, circle, radius, RelationshipKind.Contains);
    }

    [Fact]
    public void TopLevelEntryPoints_GetDistinctPerAssemblyNodes()
    {
        // Two top-level-statements executables: their entry points must be distinct nodes with
        // assembly-qualified FQNs, never one merged node (the v0.3.0 incremental-corruption fix).
        var app = GraphAssert.Node(Graph, NodeKind.Method, "FixtureApp.<top-level-statements-entry-point>");
        var cli = GraphAssert.Node(Graph, NodeKind.Method, "FixtureCli.<top-level-statements-entry-point>");

        Assert.NotEqual(app.Id, cli.Id);
        Assert.EndsWith("FixtureApp" + Path.DirectorySeparatorChar + "Program.cs", app.FilePath!, StringComparison.Ordinal);
        Assert.EndsWith("FixtureCli" + Path.DirectorySeparatorChar + "Program.cs", cli.FilePath!, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitPartialProgramClass_IsOneAssemblyQualifiedNode()
    {
        // FixtureApp declares `public partial class Program { }` (the WebApplicationFactory
        // pattern): the explicit partial and the synthesized top-level class are one symbol and
        // must yield exactly one node, assembly-qualified — GraphAssert.Node fails on 0 or 2.
        var program = GraphAssert.Node(Graph, NodeKind.Class, "FixtureApp.Program");
        Assert.EndsWith("Program.cs", program.FilePath!, StringComparison.Ordinal);

        // No project leaves an unqualified (colliding) Program node behind.
        Assert.DoesNotContain(Graph.Nodes, n => n.Kind == NodeKind.Class && n.Fqn == "Program");
    }

    [Fact]
    public void RecordsFileContentHashes()
    {
        var shapes = _fixture.Snapshot.Files.SingleOrDefault(
            f => f.Path.EndsWith("Shapes.cs", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(shapes);
        Assert.Equal(64, shapes.ContentHash.Length);
        Assert.True(_fixture.Snapshot.Stats.DocumentsAnalyzed > 0);
    }

    // --- Gap 1: type references (generic type args, typeof, attribute arguments) ---

    [Fact]
    public void TypeReference_GenericMethodArgumentOnly_CreatesReferenceEdge()
    {
        var useAll = GraphAssert.Node(Graph, NodeKind.Method, "Fixture.Lib.GenericRefs.UseAll()");
        var target = GraphAssert.Node(Graph, NodeKind.Class, "Fixture.Lib.GenericMethodArgOnly");
        GraphAssert.Edge(Graph, useAll, target, RelationshipKind.References);
    }

    [Fact]
    public void TypeReference_GenericObjectCreationArgumentOnly_CreatesReferenceEdge()
    {
        var useAll = GraphAssert.Node(Graph, NodeKind.Method, "Fixture.Lib.GenericRefs.UseAll()");
        var target = GraphAssert.Node(Graph, NodeKind.Class, "Fixture.Lib.GenericCreationArgOnly");
        GraphAssert.Edge(Graph, useAll, target, RelationshipKind.References);
    }

    [Fact]
    public void TypeReference_BareTypeof_CreatesReferenceEdge()
    {
        var useAll = GraphAssert.Node(Graph, NodeKind.Method, "Fixture.Lib.GenericRefs.UseAll()");
        var target = GraphAssert.Node(Graph, NodeKind.Class, "Fixture.Lib.TypeofOnly");
        GraphAssert.Edge(Graph, useAll, target, RelationshipKind.References);
    }

    [Fact]
    public void TypeReference_AttributeConstructorArgument_CreatesReferenceEdge()
    {
        var marked = GraphAssert.Node(Graph, NodeKind.Class, "Fixture.Lib.Marked");
        var target = GraphAssert.Node(Graph, NodeKind.Class, "Fixture.Lib.AttributeArgOnly");
        GraphAssert.Edge(Graph, marked, target, RelationshipKind.References);
    }

    [Fact]
    public void SelfReferencingGeneric_DoesNotCreateSelfLoopReferenceEdge()
    {
        // `class SelfRef : IEquatable<SelfRef>` mentions its own type inside a generic type
        // argument in its own base list — must not create a SelfRef -> SelfRef self-loop.
        var selfRef = GraphAssert.Node(Graph, NodeKind.Class, "Fixture.Lib.SelfRef");
        Assert.DoesNotContain(
            Graph.OutgoingEdges(selfRef.Id, RelationshipKind.References),
            e => e.TargetId == selfRef.Id);
    }

    [Fact]
    public void MemberReferencingOwnContainingType_CreatesRealReferenceEdge()
    {
        // A local variable's type annotation naming its own containing type is a real, wanted
        // reference, not the kind of self-loop noise the previous test guards against (source and
        // target are different nodes: a Method and its Class).
        var clone = GraphAssert.Node(Graph, NodeKind.Method, "Fixture.Lib.CopyTarget.Clone(Fixture.Lib.CopyTarget)");
        var copyTarget = GraphAssert.Node(Graph, NodeKind.Class, "Fixture.Lib.CopyTarget");
        GraphAssert.Edge(Graph, clone, copyTarget, RelationshipKind.References);
    }

    [Fact]
    public void ExplicitInterfaceSpecifier_DoesNotCreateExtraReferenceEdge()
    {
        // The "INameHolder" before ".Name" in `string INameHolder.Name => ...` must not create a
        // References edge duplicating the type-level Implements relationship.
        var explicitName = GraphAssert.Node(Graph, NodeKind.Property, "Fixture.Lib.INameHolder.Name");
        var iface = GraphAssert.Node(Graph, NodeKind.Interface, "Fixture.Lib.INameHolder");
        Assert.DoesNotContain(
            Graph.OutgoingEdges(explicitName.Id, RelationshipKind.References),
            e => e.TargetId == iface.Id);
    }

    [Fact]
    public void FullyQualifiedTypeReference_KnownResidualGap_StillProducesNoReferenceEdge()
    {
        // Documents current behavior (not a requirement): a fully-qualified (no `using`
        // shortcut) reference to an in-source type is still excluded, because it is the
        // rightmost name of a QualifiedNameSyntax, and QualifiedNameSyntax remains excluded by
        // IsNonExpressionContext for using-directive scaffolding. See
        // reports/gap-fix-implementation.md, "known residual gaps".
        var target = GraphAssert.Node(Graph, NodeKind.Class, "Fixture.Lib.FullyQualifiedRefTarget");
        Assert.Empty(Graph.IncomingEdges(target.Id, RelationshipKind.References));
    }

    // --- Gap 2: fields as graph nodes ---

    [Fact]
    public void Field_HashSetOfType_IsModeledAsFieldNodeContainedByItsType()
    {
        var field = GraphAssert.Node(Graph, NodeKind.Field, "Fixture.Lib.FieldHolder.KnownTypes");
        var holder = GraphAssert.Node(Graph, NodeKind.Class, "Fixture.Lib.FieldHolder");
        GraphAssert.Edge(Graph, holder, field, RelationshipKind.Contains);
    }

    [Fact]
    public void Field_TypeofInitializerEntries_CreateReferenceEdgesFromTheFieldItself()
    {
        // Once fields are nodes, typeof(...) entries in a field initializer must attribute to
        // the FIELD, not fall back to the containing class (the pre-fix behavior).
        var field = GraphAssert.Node(Graph, NodeKind.Field, "Fixture.Lib.FieldHolder.KnownTypes");
        var a = GraphAssert.Node(Graph, NodeKind.Class, "Fixture.Lib.FieldTypeofA");
        var b = GraphAssert.Node(Graph, NodeKind.Class, "Fixture.Lib.FieldTypeofB");
        GraphAssert.Edge(Graph, field, a, RelationshipKind.References);
        GraphAssert.Edge(Graph, field, b, RelationshipKind.References);

        var holder = GraphAssert.Node(Graph, NodeKind.Class, "Fixture.Lib.FieldHolder");
        Assert.DoesNotContain(Graph.OutgoingEdges(holder.Id, RelationshipKind.References), e => e.TargetId == a.Id);
        Assert.DoesNotContain(Graph.OutgoingEdges(holder.Id, RelationshipKind.References), e => e.TargetId == b.Id);
    }

    [Fact]
    public void MultiDeclaratorField_ProducesTwoDistinctFieldNodes()
    {
        var a = GraphAssert.Node(Graph, NodeKind.Field, "Fixture.Lib.MultiDeclaratorFields._a");
        var b = GraphAssert.Node(Graph, NodeKind.Field, "Fixture.Lib.MultiDeclaratorFields._b");
        Assert.NotEqual(a.Id, b.Id);

        var owner = GraphAssert.Node(Graph, NodeKind.Class, "Fixture.Lib.MultiDeclaratorFields");
        GraphAssert.Edge(Graph, owner, a, RelationshipKind.Contains);
        GraphAssert.Edge(Graph, owner, b, RelationshipKind.Contains);
    }

    [Fact]
    public void EventFieldDeclaration_IsDeliberatelyNotModeled()
    {
        // EventFieldDeclarationSyntax shares the field declarator shape but declares an
        // IEventSymbol, not an IFieldSymbol — out of scope for this fix (NodeKind.Event exists
        // but stays unmapped). No node of any kind should exist for it.
        Assert.DoesNotContain(Graph.Nodes, n => n.Name == "Changed" && n.Fqn.Contains("EventHolder", StringComparison.Ordinal));
    }
}
