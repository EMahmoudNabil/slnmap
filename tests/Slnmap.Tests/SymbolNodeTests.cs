using Slnmap.Core.Graph;
using Xunit;

namespace Slnmap.Tests;

public sealed class SymbolNodeTests
{
    [Fact]
    public void Create_DerivesIdFromKindAndFqn()
    {
        var node = SymbolNode.Create(NodeKind.Class, "CodeGraph", "Slnmap.Core.Graph.CodeGraph");

        Assert.Equal(SymbolNode.CreateId(NodeKind.Class, "Slnmap.Core.Graph.CodeGraph"), node.Id);
    }

    [Fact]
    public void CreateId_IsDeterministic()
    {
        string first = SymbolNode.CreateId(NodeKind.Method, "N.T.M()");
        string second = SymbolNode.CreateId(NodeKind.Method, "N.T.M()");

        Assert.Equal(first, second);
    }

    [Fact]
    public void CreateId_IsLowercaseHexOf16Bytes()
    {
        string id = SymbolNode.CreateId(NodeKind.Namespace, "Slnmap");

        Assert.Equal(32, id.Length);
        Assert.All(id, c => Assert.True(char.IsAsciiHexDigitLower(c) || char.IsAsciiDigit(c)));
    }

    [Fact]
    public void CreateId_DiffersByKind()
    {
        Assert.NotEqual(
            SymbolNode.CreateId(NodeKind.Class, "N.Widget"),
            SymbolNode.CreateId(NodeKind.Interface, "N.Widget"));
    }

    [Fact]
    public void CreateId_DiffersByFqn()
    {
        Assert.NotEqual(
            SymbolNode.CreateId(NodeKind.Class, "N.Widget"),
            SymbolNode.CreateId(NodeKind.Class, "N.Gadget"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_RejectsBlankNameOrFqn(string blank)
    {
        Assert.ThrowsAny<ArgumentException>(() => SymbolNode.Create(NodeKind.Class, blank, "N.T"));
        Assert.ThrowsAny<ArgumentException>(() => SymbolNode.Create(NodeKind.Class, "T", blank));
    }

    [Fact]
    public void Nodes_AreValueEqual()
    {
        var first = SymbolNode.Create(NodeKind.Class, "T", "N.T", "src/T.cs", new SourceSpan(0, 10));
        var second = SymbolNode.Create(NodeKind.Class, "T", "N.T", "src/T.cs", new SourceSpan(0, 10));

        Assert.Equal(first, second);
    }
}
