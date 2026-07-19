using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Slnmap.Core.Graph;

namespace Slnmap.Analysis;

internal sealed record DocumentResult(IReadOnlyList<SymbolNode> Nodes, IReadOnlyList<RelationshipEdge> Edges);

/// <summary>
/// Extracts nodes and edges from a single document. Declarations produce nodes and
/// containment/hierarchy edges; bodies produce call and reference edges via the semantic model.
/// Nodes for edge endpoints in other files are emitted too — the graph deduplicates by id.
/// </summary>
internal sealed class DocumentWalker
{
    private readonly SemanticModel _model;
    private readonly string _projectNodeId;
    private readonly CancellationToken _cancellationToken;
    private readonly Dictionary<ISymbol, SymbolNode?> _symbolNodes = new(SymbolEqualityComparer.Default);
    private readonly List<SymbolNode> _nodes = [];
    private readonly List<RelationshipEdge> _edges = [];

    private DocumentWalker(SemanticModel model, string projectNodeId, CancellationToken cancellationToken)
    {
        _model = model;
        _projectNodeId = projectNodeId;
        _cancellationToken = cancellationToken;
    }

    public static async Task<DocumentResult?> AnalyzeAsync(Document document, string projectNodeId, CancellationToken cancellationToken)
    {
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (model is null || root is null)
        {
            return null;
        }

        var walker = new DocumentWalker(model, projectNodeId, cancellationToken);
        walker.Visit(root);
        return new DocumentResult(walker._nodes, walker._edges);
    }

    private void Visit(SyntaxNode root)
    {
        foreach (var node in root.DescendantNodes())
        {
            _cancellationToken.ThrowIfCancellationRequested();
            switch (node)
            {
                case BaseTypeDeclarationSyntax or DelegateDeclarationSyntax:
                    HandleTypeDeclaration(node);
                    break;
                case MethodDeclarationSyntax or PropertyDeclarationSyntax or IndexerDeclarationSyntax:
                    GetOrCreateNode(_model.GetDeclaredSymbol(node, _cancellationToken));
                    break;
                case InvocationExpressionSyntax invocation:
                    HandleInvocation(invocation);
                    break;
                case BaseObjectCreationExpressionSyntax creation:
                    HandleObjectCreation(creation);
                    break;
                case SimpleNameSyntax name:
                    HandleNameReference(name);
                    break;
            }
        }
    }

    private void HandleTypeDeclaration(SyntaxNode declaration)
    {
        if (_model.GetDeclaredSymbol(declaration, _cancellationToken) is not INamedTypeSymbol symbol)
        {
            return;
        }

        var node = GetOrCreateNode(symbol);
        if (node is null)
        {
            return;
        }

        AddContainment(symbol, node);

        // SpecialType filters out object, ValueType, Enum, and Delegate base classes.
        if (symbol.BaseType is { SpecialType: SpecialType.None } baseType)
        {
            AddTypeEdge(node, baseType, RelationshipKind.Inherits);
        }

        foreach (var contract in symbol.Interfaces)
        {
            AddTypeEdge(node, contract, RelationshipKind.Implements);
        }
    }

    private void AddTypeEdge(SymbolNode source, INamedTypeSymbol target, RelationshipKind kind)
    {
        if (GetOrCreateNode(target) is { } targetNode)
        {
            _edges.Add(new RelationshipEdge(source.Id, targetNode.Id, kind));
        }
    }

    private void AddContainment(INamedTypeSymbol symbol, SymbolNode node)
    {
        if (symbol.ContainingType is { } outer)
        {
            if (GetOrCreateNode(outer) is { } outerNode)
            {
                _edges.Add(new RelationshipEdge(outerNode.Id, node.Id, RelationshipKind.Contains));
            }

            return;
        }

        var ns = symbol.ContainingNamespace;
        if (ns is null || ns.IsGlobalNamespace)
        {
            _edges.Add(new RelationshipEdge(_projectNodeId, node.Id, RelationshipKind.Contains));
            return;
        }

        if (GetOrCreateNode(ns) is { } nsNode)
        {
            _edges.Add(new RelationshipEdge(nsNode.Id, node.Id, RelationshipKind.Contains));
        }

        // Walk the namespace chain up to the project: project ⊃ A ⊃ A.B ⊃ type.
        var current = ns;
        while (true)
        {
            var parent = current.ContainingNamespace;
            var currentNode = GetOrCreateNode(current);
            if (currentNode is null)
            {
                break;
            }

            if (parent is null || parent.IsGlobalNamespace)
            {
                _edges.Add(new RelationshipEdge(_projectNodeId, currentNode.Id, RelationshipKind.Contains));
                break;
            }

            if (GetOrCreateNode(parent) is { } parentNode)
            {
                _edges.Add(new RelationshipEdge(parentNode.Id, currentNode.Id, RelationshipKind.Contains));
            }

            current = parent;
        }
    }

    private void HandleInvocation(InvocationExpressionSyntax invocation)
    {
        if (ResolveSymbol(invocation) is not IMethodSymbol method)
        {
            return;
        }

        if (method.ReducedFrom is { } reduced)
        {
            method = reduced;
        }

        if (method.MethodKind == MethodKind.DelegateInvoke)
        {
            return;
        }

        var target = GetOrCreateNode(method);
        if (target is null)
        {
            return;
        }

        if (GetEnclosingMemberNode(invocation.SpanStart) is { } source)
        {
            _edges.Add(new RelationshipEdge(source.Id, target.Id, RelationshipKind.Calls));
        }
    }

    private void HandleObjectCreation(BaseObjectCreationExpressionSyntax creation)
    {
        if (ResolveSymbol(creation) is not IMethodSymbol constructor)
        {
            return;
        }

        var target = GetOrCreateNode(constructor.ContainingType);
        if (target is null)
        {
            return;
        }

        if (GetEnclosingMemberNode(creation.SpanStart) is { } source)
        {
            _edges.Add(new RelationshipEdge(source.Id, target.Id, RelationshipKind.References));
        }
    }

    private void HandleNameReference(SimpleNameSyntax name)
    {
        if (IsNonExpressionContext(name) || IsInvocationTarget(name))
        {
            return;
        }

        // Only property accesses and method-group references become References edges;
        // invocations are Calls, and plain type mentions are covered by other edge kinds.
        var symbol = ResolveSymbol(name);
        if (symbol is not (IPropertySymbol or IMethodSymbol))
        {
            return;
        }

        var target = GetOrCreateNode(symbol);
        if (target is null)
        {
            return;
        }

        if (GetEnclosingMemberNode(name.SpanStart) is { } source && source.Id != target.Id)
        {
            _edges.Add(new RelationshipEdge(source.Id, target.Id, RelationshipKind.References));
        }
    }

    private ISymbol? ResolveSymbol(SyntaxNode node)
    {
        var info = _model.GetSymbolInfo(node, _cancellationToken);
        return info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
    }

    private static bool IsNonExpressionContext(SimpleNameSyntax name) => name.Parent switch
    {
        QualifiedNameSyntax => true,        // type/namespace positions (using directives, qualified type names)
        UsingDirectiveSyntax => true,
        TypeArgumentListSyntax => true,
        NameColonSyntax or NameEqualsSyntax => true,
        ExplicitInterfaceSpecifierSyntax => true,
        AliasQualifiedNameSyntax => true,
        _ => false,
    };

    private static bool IsInvocationTarget(SimpleNameSyntax name)
    {
        SyntaxNode expression = name;
        if (name.Parent is MemberAccessExpressionSyntax memberAccess && memberAccess.Name == name)
        {
            expression = memberAccess;
        }
        else if (name.Parent is MemberBindingExpressionSyntax memberBinding && memberBinding.Name == name)
        {
            expression = memberBinding;
        }

        return expression.Parent is InvocationExpressionSyntax invocation && invocation.Expression == expression;
    }

    /// <summary>
    /// Resolves the member node an edge at <paramref name="position"/> originates from:
    /// accessors map to their property, lambdas and local functions to their containing
    /// member, field initializers to their type. Covers synthesized members such as the
    /// top-level-statements entry point.
    /// </summary>
    private SymbolNode? GetEnclosingMemberNode(int position)
    {
        var symbol = _model.GetEnclosingSymbol(position, _cancellationToken);
        while (symbol is not null)
        {
            if (symbol is IMethodSymbol { AssociatedSymbol: IPropertySymbol property })
            {
                symbol = property;
                continue;
            }

            if (symbol is IMethodSymbol { MethodKind: MethodKind.AnonymousFunction or MethodKind.LocalFunction })
            {
                symbol = symbol.ContainingSymbol;
                continue;
            }

            if (symbol is IMethodSymbol or IPropertySymbol or INamedTypeSymbol
                && GetOrCreateNode(symbol) is { } node)
            {
                return node;
            }

            symbol = symbol is IFieldSymbol field ? field.ContainingType : symbol.ContainingSymbol;
        }

        return null;
    }

    /// <summary>
    /// Returns the node for a symbol (creating and recording it on first sight), or null when
    /// the symbol is not source-declared or unmodeled. Member nodes also get a Contains edge
    /// from their containing type, so on-demand targets (e.g. constructors) stay connected.
    /// </summary>
    private SymbolNode? GetOrCreateNode(ISymbol? symbol)
    {
        if (symbol is null)
        {
            return null;
        }

        symbol = symbol.OriginalDefinition;
        if (_symbolNodes.TryGetValue(symbol, out var existing))
        {
            return existing;
        }

        var node = SymbolFacts.TryCreateNode(symbol);
        _symbolNodes[symbol] = node;
        if (node is null)
        {
            return null;
        }

        _nodes.Add(node);
        if (node.Kind is NodeKind.Method or NodeKind.Constructor or NodeKind.Property
            && symbol.ContainingType is { } containingType
            && GetOrCreateNode(containingType) is { } typeNode)
        {
            _edges.Add(new RelationshipEdge(typeNode.Id, node.Id, RelationshipKind.Contains));
        }

        return node;
    }
}
