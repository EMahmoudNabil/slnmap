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
                case MethodDeclarationSyntax or PropertyDeclarationSyntax or IndexerDeclarationSyntax or EventDeclarationSyntax:
                    GetOrCreateNode(_model.GetDeclaredSymbol(node, _cancellationToken));
                    break;
                // BaseFieldDeclarationSyntax covers both FieldDeclarationSyntax and its sibling
                // EventFieldDeclarationSyntax (same declarator shape, `event Handler Foo;`).
                // GetDeclaredSymbol resolves each declarator to the correct symbol kind (IFieldSymbol
                // or IEventSymbol) regardless of which base-type arm matched; MapKind routes each to
                // NodeKind.Field or NodeKind.Event correctly.
                case VariableDeclaratorSyntax declarator when declarator.Parent?.Parent is BaseFieldDeclarationSyntax:
                    GetOrCreateNode(_model.GetDeclaredSymbol(declarator, _cancellationToken));
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

        if (GetEnclosingMemberNode(invocation) is { } source)
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

        if (GetEnclosingMemberNode(creation) is { } source)
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

        // Property accesses, field/const reads and writes, method-group references, and plain
        // type mentions (generic type arguments, typeof(), attribute constructor arguments,
        // parameter/field/return types) become References edges; invocations are Calls, and a
        // type's own declaration is covered by Inherits/Implements/Contains instead. Enum
        // members are IFieldSymbol too but stay edge-less: MapKind deliberately leaves them
        // unmodeled, so GetOrCreateNode returns null below and the edge is skipped.
        var symbol = ResolveSymbol(name);
        if (symbol is not (IPropertySymbol or IMethodSymbol or INamedTypeSymbol or IFieldSymbol))
        {
            return;
        }

        var target = GetOrCreateNode(symbol);
        if (target is null)
        {
            return;
        }

        if (GetEnclosingMemberNode(name) is { } source && source.Id != target.Id)
        {
            _edges.Add(new RelationshipEdge(source.Id, target.Id, RelationshipKind.References));
        }
    }

    private ISymbol? ResolveSymbol(SyntaxNode node)
    {
        var info = _model.GetSymbolInfo(node, _cancellationToken);
        return info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
    }

    private static bool IsNonExpressionContext(SimpleNameSyntax name)
    {
        if (name.Parent is QualifiedNameSyntax parentQn)
        {
            // Left-side segments, and right-side segments that are themselves nested inside a
            // longer chain, are namespace/type qualifiers — never a reference in their own right.
            if (parentQn.Right != name || parentQn.Parent is QualifiedNameSyntax)
            {
                return true;
            }

            // `name` is the true, whole-chain-terminating leaf. Its status now depends on what the
            // chain as a whole names — a namespace being imported/declared stays excluded; anything
            // else (typeof, parameter/field/variable type, generic argument, cast, attribute name,
            // object-creation type, …) is a real type reference and must NOT be excluded.
            return parentQn.Parent is UsingDirectiveSyntax
                or NamespaceDeclarationSyntax
                or FileScopedNamespaceDeclarationSyntax;
        }

        return name.Parent switch
        {
            UsingDirectiveSyntax => true,
            NameColonSyntax or NameEqualsSyntax => true,
            ExplicitInterfaceSpecifierSyntax => true,
            AliasQualifiedNameSyntax => true,
            _ => false,
        };
    }

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
    /// Resolves the member node an edge originating at <paramref name="syntax"/> should attribute
    /// to: accessors map to their property, lambdas and local functions to their containing
    /// member, field initializers to their field (falling further back to the containing type
    /// only when the field itself isn't modeled, e.g. an enum member). Covers synthesized
    /// members such as the top-level-statements entry point.
    /// </summary>
    /// <remarks>
    /// A position inside a method/property/indexer/event's own SIGNATURE (a parameter type or
    /// return type) is a special case checked first, syntactically: <see cref="SemanticModel.GetEnclosingSymbol"/>
    /// resolves such a position to the CONTAINING TYPE, skipping the member being declared there
    /// entirely — a walk-up from that point can never recover it, since the correct answer isn't
    /// an ancestor of what <c>GetEnclosingSymbol</c> returned. Confirmed pre-existing (reproduces
    /// for an unqualified case too, e.g. a self-referential parameter type — just silently masked
    /// there because the misattributed source happens to equal the target, so the self-loop guard
    /// in <see cref="HandleNameReference"/> hides it); unrelated to type-reference edge kind.
    /// </remarks>
    private SymbolNode? GetEnclosingMemberNode(SyntaxNode syntax)
    {
        for (var ancestor = syntax.Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (ancestor is not (BaseMethodDeclarationSyntax or BasePropertyDeclarationSyntax))
            {
                continue;
            }

            SyntaxNode? body = ancestor switch
            {
                BaseMethodDeclarationSyntax m => m.Body ?? (SyntaxNode?)m.ExpressionBody,
                PropertyDeclarationSyntax p => p.AccessorList ?? (SyntaxNode?)p.ExpressionBody,
                IndexerDeclarationSyntax i => i.AccessorList ?? (SyntaxNode?)i.ExpressionBody,
                EventDeclarationSyntax e => e.AccessorList,
                _ => null,
            };

            // Not in the body (or there is none, e.g. an abstract/partial signature) — `syntax`
            // sits in the declaration's own signature. Attribute directly to it; a walk-up from
            // GetEnclosingSymbol's answer would land on the containing type instead.
            if (body is null || !body.Span.Contains(syntax.Span))
            {
                return _model.GetDeclaredSymbol(ancestor, _cancellationToken) is { } declared
                    ? GetOrCreateNode(declared)
                    : null;
            }

            break;
        }

        var symbol = _model.GetEnclosingSymbol(syntax.SpanStart, _cancellationToken);
        while (symbol is not null)
        {
            if (symbol is IMethodSymbol { AssociatedSymbol: IPropertySymbol property })
            {
                symbol = property;
                continue;
            }

            // The event-accessor analogue of the property case above. ContainingSymbol of an
            // EventAdd/EventRemove accessor resolves to the containing TYPE, not the event —
            // AssociatedSymbol is the documented, reliable way to reach the event itself (the
            // same API the property case already relies on for the identical purpose).
            if (symbol is IMethodSymbol { AssociatedSymbol: IEventSymbol @event })
            {
                symbol = @event;
                continue;
            }

            if (symbol is IMethodSymbol { MethodKind: MethodKind.AnonymousFunction or MethodKind.LocalFunction })
            {
                symbol = symbol.ContainingSymbol;
                continue;
            }

            if (symbol is IMethodSymbol or IPropertySymbol or INamedTypeSymbol or IFieldSymbol or IEventSymbol
                && GetOrCreateNode(symbol) is { } node)
            {
                return node;
            }

            symbol = symbol.ContainingSymbol;
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
        if (node.Kind is NodeKind.Method or NodeKind.Constructor or NodeKind.Property or NodeKind.Field or NodeKind.Event
            && symbol.ContainingType is { } containingType
            && GetOrCreateNode(containingType) is { } typeNode)
        {
            _edges.Add(new RelationshipEdge(typeNode.Id, node.Id, RelationshipKind.Contains));
        }

        return node;
    }
}
