using Slnmap.Core.Graph;
using Slnmap.Mcp;
using Xunit;

namespace Slnmap.Tests;

/// <summary>
/// v0.6.1 LIE-1 fix: fields and consts must receive INCOMING References edges from the members
/// that read/write them. v0.5.0 (Gap 2) gave fields nodes and v0.6.0 (Q3) gave them OUTGOING
/// edges, but HandleNameReference's target filter still rejected IFieldSymbol — so find_usages
/// answered "No usages found" for every field/const in the graph, a repo-wide false "safe to
/// change" on const-based codebases (the OSSUS VendorActivityType.Deactivated case). Fixture
/// cases: VendorActivity.cs, DeactivateVendorCommand.cs (FixtureLib), VendorAudit.cs (FixtureApp).
/// </summary>
public sealed class FieldUsageEdgeTests : IClassFixture<AnalyzedFixtureSolution>
{
    private readonly AnalyzedFixtureSolution _fixture;

    public FieldUsageEdgeTests(AnalyzedFixtureSolution fixture) => _fixture = fixture;

    private CodeGraph Graph => _fixture.Graph;

    private List<SymbolNode> ReferencingMembers(SymbolNode target) =>
        Graph.IncomingEdges(target.Id, RelationshipKind.References)
            .Select(e => Graph.TryGetNode(e.SourceId, out var source) ? source : null)
            .OfType<SymbolNode>()
            .ToList();

    [Fact]
    public void ConstUsedInThreeFiles_HasExactlyThreeUsages_WithCorrectEnclosingMembers()
    {
        var target = GraphAssert.Node(Graph, NodeKind.Field, "Fixture.Lib.VendorActivityTypes.Deactivated");
        var users = ReferencingMembers(target);

        Assert.Equal(3, users.Count);
        Assert.Contains(users, u => u.Fqn == "Fixture.Lib.FieldUsageHolder.IsDeactivation(string)");
        Assert.Contains(users, u => u.Fqn == "Fixture.Lib.DeactivateVendorCommand.ActivityType");
        Assert.Contains(users, u => u.Fqn == "Fixture.App.VendorAudit.IsDeactivation(string)");
        Assert.Equal(3, users.Select(u => u.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void PrivateField_ReadAndWriteSites_AllRecorded()
    {
        var target = GraphAssert.Node(Graph, NodeKind.Field, "Fixture.Lib.FieldUsageHolder._counter");
        var users = ReferencingMembers(target).Select(u => u.Fqn).ToList();

        // Compound write, plain write, argument-position read, interpolated-string read.
        Assert.Contains("Fixture.Lib.FieldUsageHolder.Increment()", users);
        Assert.Contains("Fixture.Lib.FieldUsageHolder.Reset()", users);
        Assert.Contains("Fixture.Lib.FieldUsageHolder.Magnitude()", users);
        Assert.Contains("Fixture.Lib.FieldUsageHolder.Describe()", users);
        Assert.Equal(4, users.Count);
    }

    [Fact]
    public void InitializerOnlyField_HasNoUsages_AndDoesNotSelfReport()
    {
        var target = GraphAssert.Node(Graph, NodeKind.Field, "Fixture.Lib.FieldUsageHolder._initializerOnly");
        Assert.Empty(Graph.IncomingEdges(target.Id, RelationshipKind.References));
    }

    [Fact]
    public void UnreferencedConst_HasNoUsages()
    {
        var target = GraphAssert.Node(Graph, NodeKind.Field, "Fixture.Lib.VendorActivityTypes.Unused");
        Assert.Empty(Graph.IncomingEdges(target.Id, RelationshipKind.References));
    }

    [Fact]
    public void NameofReference_IsAUsageEdge()
    {
        // Decision pinned (v0.6.1): nameof(field) IS a usage — Roslyn resolves the argument to
        // the field symbol, and renaming/removing the field breaks the nameof site.
        var source = GraphAssert.Node(Graph, NodeKind.Method, "Fixture.Lib.FieldUsageHolder.NamedFieldName()");
        var target = GraphAssert.Node(Graph, NodeKind.Field, "Fixture.Lib.FieldUsageHolder._named");
        GraphAssert.Edge(Graph, source, target, RelationshipKind.References);
    }

    [Fact]
    public void NameofInAttributeArgument_AttributesToTheDecoratedMethod()
    {
        // The attribute sits in the method's signature position — #4's signature-position
        // attribution must credit the method itself, not the containing type.
        var source = GraphAssert.Node(Graph, NodeKind.Method, "Fixture.Lib.FieldUsageHolder.Legacy()");
        var target = GraphAssert.Node(Graph, NodeKind.Field, "Fixture.Lib.FieldUsageHolder._named");
        GraphAssert.Edge(Graph, source, target, RelationshipKind.References);
    }

    [Fact]
    public void ConstReadInAnotherFieldInitializer_AttributesToTheInitializedField()
    {
        // Gap-2's GetEnclosingMemberNode behavior, now with a FIELD target: the initializer's
        // read of the const attributes to the initialized field itself.
        var source = GraphAssert.Node(Graph, NodeKind.Field, "Fixture.Lib.FieldUsageHolder._label");
        var target = GraphAssert.Node(Graph, NodeKind.Field, "Fixture.Lib.VendorActivityTypes.Activated");
        GraphAssert.Edge(Graph, source, target, RelationshipKind.References);
    }

    [Fact]
    public void StaticFieldSelfInitializer_ProducesNoSelfLoop()
    {
        // `private static string? _selfRef = _selfRef;` — source and target resolve to the same
        // node; the existing self-loop guard (the one #4 relies on for CRTP) must suppress it.
        var field = GraphAssert.Node(Graph, NodeKind.Field, "Fixture.Lib.FieldUsageHolder._selfRef");
        Assert.DoesNotContain(
            Graph.IncomingEdges(field.Id, RelationshipKind.References),
            e => e.SourceId == field.Id);
    }

    [Fact]
    public void EnumMemberUsage_ReachesBothTheMemberAndTheEnumType()
    {
        // Decision REVERSED in v0.9.0 (#13): the v0.6.1 census-consistency objection is resolved
        // by the EnumMemberDeclarationSyntax declaration walk, so members are first-class
        // EnumMember nodes now. The enum-TYPE edge (via the "VendorState" segment) is unchanged —
        // member granularity is additive, exactly like the const fix this file pins.
        var source = GraphAssert.Node(Graph, NodeKind.Property, "Fixture.Lib.VendorStateReader.Current");
        var member = GraphAssert.Node(Graph, NodeKind.EnumMember, "Fixture.Lib.VendorState.Deactivated");
        GraphAssert.Edge(Graph, source, member, RelationshipKind.References);

        var enumType = GraphAssert.Node(Graph, NodeKind.Enum, "Fixture.Lib.VendorState");
        GraphAssert.Edge(Graph, source, enumType, RelationshipKind.References);
    }

    [Fact]
    public void PreexistingFixtureFields_NowHaveIncomingUsages()
    {
        // The fix generalizes to fields that predate the v0.6.1 fixture additions.
        var knownTypes = GraphAssert.Node(Graph, NodeKind.Field, "Fixture.Lib.FieldHolder.KnownTypes");
        Assert.Contains(ReferencingMembers(knownTypes), u => u.Fqn == "Fixture.Lib.FieldHolder.Contains(System.Type)");

        var a = GraphAssert.Node(Graph, NodeKind.Field, "Fixture.Lib.MultiDeclaratorFields._a");
        var b = GraphAssert.Node(Graph, NodeKind.Field, "Fixture.Lib.MultiDeclaratorFields._b");
        Assert.Contains(ReferencingMembers(a), u => u.Fqn == "Fixture.Lib.MultiDeclaratorFields.Sum()");
        Assert.Contains(ReferencingMembers(b), u => u.Fqn == "Fixture.Lib.MultiDeclaratorFields.Sum()");
    }
}

/// <summary>The same LIE-1 cases through the MCP query layer — the surface the eval exercised.</summary>
public sealed class FieldUsageQueryTests : IClassFixture<AnalyzedFixtureGraphStore>
{
    private readonly SlnmapQueries _queries;

    public FieldUsageQueryTests(AnalyzedFixtureGraphStore fixture) => _queries = new SlnmapQueries(fixture.Store);

    [Fact]
    public async Task FindUsages_ConstUsedInThreeFiles_ListsAllThreeMembers()
    {
        string result = await _queries.FindUsagesAsync("Fixture.Lib.VendorActivityTypes.Deactivated");

        Assert.StartsWith("3 usage(s) of Fixture.Lib.VendorActivityTypes.Deactivated", result, StringComparison.Ordinal);
        Assert.Contains("Fixture.Lib.FieldUsageHolder.IsDeactivation(string)", result, StringComparison.Ordinal);
        Assert.Contains("Fixture.Lib.DeactivateVendorCommand.ActivityType", result, StringComparison.Ordinal);
        Assert.Contains("Fixture.App.VendorAudit.IsDeactivation(string)", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindUsages_PrivateField_ListsReadAndWriteSites()
    {
        string result = await _queries.FindUsagesAsync("Fixture.Lib.FieldUsageHolder._counter");

        Assert.Contains("Fixture.Lib.FieldUsageHolder.Reset()", result, StringComparison.Ordinal);
        Assert.Contains("Fixture.Lib.FieldUsageHolder.Magnitude()", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindUsages_UnreferencedConst_ReportsNone()
    {
        string result = await _queries.FindUsagesAsync("Fixture.Lib.VendorActivityTypes.Unused");
        Assert.Equal("No usages found for Fixture.Lib.VendorActivityTypes.Unused.", result);
    }

    [Fact]
    public async Task ImpactAnalysis_Const_ReportsDependents()
    {
        // The eval's false "safe to change", flipped: a const's dependents must be reported.
        string result = await _queries.ImpactAnalysisAsync("Fixture.Lib.VendorActivityTypes.Deactivated");

        Assert.DoesNotContain("nothing else in the graph depends on it", result, StringComparison.Ordinal);
        Assert.Contains("dependent symbol(s).", result, StringComparison.Ordinal);
        Assert.Contains("Fixture.App.VendorAudit.IsDeactivation(string)", result, StringComparison.Ordinal);
    }
}
