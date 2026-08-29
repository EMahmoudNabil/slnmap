using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Slnmap.Core.Graph;

namespace Slnmap.Analysis;

internal sealed record DocumentResult(
    IReadOnlyList<SymbolNode> Nodes,
    IReadOnlyList<RelationshipEdge> Edges,
    IReadOnlyList<string> Warnings,
    int UnresolvedEndpoints,
    int ConventionalControllers,
    int RazorPagesNotModeled,
    int ControllerLikeClassesUnrecognized = 0);

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
    private readonly List<string> _warnings = [];
    private readonly HashSet<INamedTypeSymbol> _conventionalControllers = new(SymbolEqualityComparer.Default);
    private readonly HashSet<INamedTypeSymbol> _razorPagesNotModeled = new(SymbolEqualityComparer.Default);
    private readonly HashSet<INamedTypeSymbol> _controllerLikeUnrecognized = new(SymbolEqualityComparer.Default);
    private int _unresolvedEndpoints;

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
        return new DocumentResult(
            walker._nodes, walker._edges, walker._warnings, walker._unresolvedEndpoints,
            walker._conventionalControllers.Count, walker._razorPagesNotModeled.Count,
            walker._controllerLikeUnrecognized.Count);
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
                case MethodDeclarationSyntax method:
                    var declared = _model.GetDeclaredSymbol(method, _cancellationToken);
                    GetOrCreateNode(declared);
                    // Attribute-routed controller actions become Endpoint nodes (v1.1). Syntactic
                    // prefilter: a class with a base list MIGHT derive from ControllerBase, so
                    // everything else pays zero semantic cost here — EXCEPT a class that looks
                    // controller-ish on its own syntactic shape (v0.13.1: name ends in
                    // "Controller", or an [ApiController]/[Route]/[Controller] attribute on the
                    // class, or an [Http*] attribute on any member — all pure syntax, no semantic
                    // model call, so still "free"). ASP.NET Core's real POCO-controller discovery
                    // never required a base list at all (reports/v0131-poco-controller-investigation.md)
                    // — `gothinkster/aspnetcore-realworld-example-app`'s actual UserController/
                    // UsersController have none, and were invisible to this extractor entirely
                    // before this widening.
                    if (declared is IMethodSymbol methodSymbol && method.Parent is ClassDeclarationSyntax classDecl)
                    {
                        bool hasBaseList = classDecl.BaseList is not null;
                        string? controllerishSignal = hasBaseList ? null : LooksControllerish(classDecl);
                        if (hasBaseList || controllerishSignal is not null)
                        {
                            HandleControllerAction(method, methodSymbol, syntacticOnlySignal: controllerishSignal);
                            HandleRazorPageHandler(methodSymbol);
                        }
                    }

                    break;
                case PropertyDeclarationSyntax or IndexerDeclarationSyntax or EventDeclarationSyntax:
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
                // Enum members are declared via their own syntax node, never a field declarator —
                // without this case only REFERENCED members would materialize (the v0.6.1
                // census-inconsistency objection that kept them unmodeled, #13).
                case EnumMemberDeclarationSyntax enumMember:
                    GetOrCreateNode(_model.GetDeclaredSymbol(enumMember, _cancellationToken));
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

        // v0.13.1 (reports/v0130-regression-investigation-0of22-realworld.md): a call shaped like
        // `opt.Conventions.Add(someConvention)` registers an IApplicationModelConvention, which
        // can mutate route templates at MVC application-model-build time (e.g. inject a base-path
        // prefix) invisibly to static analysis — confirmed as the real root cause of a 0/22
        // regression against gothinkster/aspnetcore-realworld-example-app's actual
        // `ApiRoutePrefixConvention`. Cheap honesty only: recognized by the PARAMETER TYPE being
        // (or implementing) `IApplicationModelConvention` — never by the receiver's variable name
        // or declared type (`MvcOptions`, `RazorPagesOptions`, ... all expose the same
        // `IList<IApplicationModelConvention> Conventions` shape) — no attempt is made to
        // interpret what the convention actually does.
        if (IsApplicationModelConventionsAdd(method))
        {
            _warnings.Add(
                $"MvcOptions.Conventions.Add(...) at {Location(invocation)} registers an IApplicationModelConvention, "
                + "which can mutate route templates at runtime (e.g. inject a base-path prefix) invisibly to static "
                + "analysis — extracted controller endpoint templates may not reflect what the app actually serves. "
                + "slnmap does not interpret convention implementations.");
        }

        // Minimal-API endpoint registrations (Map* calls) would otherwise die at the external-target
        // early return below — the framework Map* is not in source. Handled additively, before the
        // un-reduction: EndpointFacts maps arguments to parameters on the symbol exactly as resolved.
        // Name prefilter first, so the common case pays a single string comparison.
        if (method.Name.StartsWith("Map", StringComparison.Ordinal))
        {
            HandleEndpointRegistration(invocation, method);
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

    /// <summary>
    /// True for a call to <c>Add</c> whose single parameter type IS (or implements)
    /// <c>Microsoft.AspNetCore.Mvc.ApplicationModels.IApplicationModelConvention</c> — checked by
    /// the parameter's real type, never the receiver's declared type or variable name, so this
    /// recognizes <c>MvcOptions.Conventions.Add(...)</c>, <c>RazorPagesOptions.Conventions.Add(...)</c>,
    /// and any equivalent <c>IList&lt;IApplicationModelConvention&gt;</c>-shaped collection alike.
    /// </summary>
    private static bool IsApplicationModelConventionsAdd(IMethodSymbol method)
    {
        if (method.Name != "Add" || method.Parameters.Length != 1)
        {
            return false;
        }

        return method.Parameters[0].Type is INamedTypeSymbol parameterType
            && (IsApplicationModelConventionType(parameterType)
                || parameterType.AllInterfaces.Any(IsApplicationModelConventionType));
    }

    private static bool IsApplicationModelConventionType(INamedTypeSymbol type) =>
        type is { Name: "IApplicationModelConvention", ContainingNamespace: { } ns }
        && ns.ToDisplayString() == "Microsoft.AspNetCore.Mvc.ApplicationModels";

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

        // Property accesses, field/const reads and writes (enum members included — they're
        // IFieldSymbol with their own EnumMember nodes since #13), event subscriptions/raisings
        // (+=, -=, Event?.Invoke — the event NAME is the reference; DelegateInvoke itself stays
        // deliberately unmodeled, #8), method-group references, and plain type mentions (generic
        // type arguments, typeof(), attribute constructor arguments, parameter/field/return
        // types) become References edges; invocations are Calls, and a type's own declaration is
        // covered by Inherits/Implements/Contains instead.
        var symbol = ResolveSymbol(name);
        if (symbol is not (IPropertySymbol or IMethodSymbol or INamedTypeSymbol or IFieldSymbol or IEventSymbol))
        {
            return;
        }

        var target = GetOrCreateNode(symbol);
        if (target is null)
        {
            return;
        }

        if (GetEnclosingMemberNode(name) is { } source)
        {
            if (source.Id != target.Id)
            {
                _edges.Add(new RelationshipEdge(source.Id, target.Id, RelationshipKind.References));
            }

            return;
        }

        // No enclosing member exists for ASSEMBLY-LEVEL attributes ([assembly: X(typeof(T))]) —
        // they sit above every declaration, which silently dropped the reference (#11). The
        // assembly IS the project, so the project node is the honest source. Known limitation:
        // project-sourced edges survive incremental eviction unconditionally (the project node
        // has no file), so REMOVING such an attribute leaves the edge until the next full
        // re-analysis — acceptable for a rare declaration shape, documented in the report.
        if (IsInsideAssemblyLevelAttribute(name))
        {
            _edges.Add(new RelationshipEdge(_projectNodeId, target.Id, RelationshipKind.References));
        }
    }

    private static bool IsInsideAssemblyLevelAttribute(SyntaxNode name)
    {
        for (var ancestor = name.Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (ancestor is AttributeListSyntax attributeList)
            {
                return attributeList.Target?.Identifier.ValueText is "assembly" or "module";
            }

            if (ancestor is MemberDeclarationSyntax)
            {
                return false;
            }
        }

        return false;
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
        if (node.Kind is NodeKind.Method or NodeKind.Constructor or NodeKind.Property or NodeKind.Field or NodeKind.Event or NodeKind.EnumMember
            && symbol.ContainingType is { } containingType
            && GetOrCreateNode(containingType) is { } typeNode)
        {
            _edges.Add(new RelationshipEdge(typeNode.Id, node.Id, RelationshipKind.Contains));
        }

        return node;
    }

    /// <summary>
    /// Synthesizes an Endpoint node from a Minimal-API Map* registration: fqn = "VERB template",
    /// name = template, file+span = this call site. Emits RegisteringType —Contains→ Endpoint
    /// explicitly (an endpoint is not a symbol, so <see cref="GetOrCreateNode"/>'s symbol-keyed
    /// containment can't cover it) and Endpoint —HandledBy→ Method when the handler is a method
    /// group resolving to a modeled method. Every registration that fails static resolution is
    /// counted and reported with a reason — never guessed (the deterministic-or-declared contract).
    /// </summary>
    private void HandleEndpointRegistration(InvocationExpressionSyntax invocation, IMethodSymbol methodAsResolved)
    {
        var extraction = EndpointFacts.TryExtract(invocation, methodAsResolved, _model, _cancellationToken);
        if (extraction is null)
        {
            return;
        }

        if (extraction.Template is null)
        {
            _unresolvedEndpoints++;
            _warnings.Add($"Unresolved endpoint registration at {Location(invocation)}: {extraction.UnresolvedReason} (counted, not guessed).");
            return;
        }

        var location = invocation.GetLocation();
        var node = SymbolNode.Create(
            NodeKind.Endpoint,
            name: extraction.Template,
            fqn: $"{extraction.Verb} {extraction.Template}",
            filePath: location.SourceTree?.FilePath,
            span: new SourceSpan(location.SourceSpan.Start, location.SourceSpan.End));
        _nodes.Add(node);

        // Duplicate registrations of the same verb+template hash to the same id — the graph keeps
        // the first node (its call site) and every HandledBy edge (the honest superposition).
        if (GetEnclosingTypeNode(invocation) is { } registrar)
        {
            _edges.Add(new RelationshipEdge(registrar.Id, node.Id, RelationshipKind.Contains));
        }

        if (extraction.HandlerExpression is { } handlerExpression
            && ResolveSymbol(handlerExpression) is IMethodSymbol handler
            && handler.MethodKind is not (MethodKind.AnonymousFunction or MethodKind.LocalFunction)
            && GetOrCreateNode(handler) is { } handlerNode)
        {
            _edges.Add(new RelationshipEdge(node.Id, handlerNode.Id, RelationshipKind.HandledBy));
        }
        else
        {
            _warnings.Add($"Endpoint {node.Fqn} at {Location(invocation)}: handler is not a resolvable method group (lambda/local function/unmodeled) — endpoint recorded without a HandledBy edge.");
        }
    }

    /// <summary>
    /// Pure-syntax "looks controller-ish" check (v0.13.1) — never a semantic model call, so it
    /// stays exactly as cheap as the base-list check it widens ("syntactic prefilter, free").
    /// Returns a short human-readable description of the first matching signal (folded into the
    /// eventual disclosure message if classification still fails), or null when none match.
    /// Checked in the same priority order ASP.NET Core's own discovery favors: class name ending
    /// in "Controller" first, then an [ApiController]/[Route]/[Controller] attribute on the class,
    /// then an [Http*] attribute on any member (the weakest signal alone, but real: a class could
    /// be attribute-routed without an ASP.NET-recognizable name/attribute combo on the class
    /// itself). Verified zero false positives on OSSUS_BE.sln (0 matches across all three signals
    /// — reports/v0131-poco-controller-investigation.md) and against every existing fixture.
    /// </summary>
    private static string? LooksControllerish(ClassDeclarationSyntax classDecl)
    {
        if (classDecl.Identifier.ValueText.EndsWith("Controller", StringComparison.OrdinalIgnoreCase))
        {
            return "name ends in \"Controller\"";
        }

        if (HasAnyAttributeNamed(classDecl.AttributeLists, "ApiController", "Route", "Controller"))
        {
            return "has an [ApiController]/[Route]/[Controller] attribute";
        }

        if (classDecl.Members.OfType<MethodDeclarationSyntax>().Any(m =>
            HasAnyAttributeNamed(m.AttributeLists, "HttpGet", "HttpPost", "HttpPut", "HttpDelete", "HttpPatch", "HttpHead", "HttpOptions")))
        {
            return "has an [Http*] attribute on a member";
        }

        return null;
    }

    private static bool HasAnyAttributeNamed(SyntaxList<AttributeListSyntax> attributeLists, params string[] shortNames) =>
        attributeLists.SelectMany(al => al.Attributes).Any(a => shortNames.Any(n => AttributeNameIs(a, n)));

    private static bool AttributeNameIs(AttributeSyntax attribute, string shortName)
    {
        string name = attribute.Name switch
        {
            QualifiedNameSyntax q => q.Right.Identifier.ValueText,
            SimpleNameSyntax s => s.Identifier.ValueText,
            _ => attribute.Name.ToString(),
        };
        return name == shortName || name == shortName + "Attribute";
    }

    /// <summary>
    /// Synthesizes Endpoint nodes from an attribute-routed controller action (v1.1): same node
    /// and edge shape as the Minimal-API branch — fqn = "VERB template" composed per MVC's own
    /// selector semantics (ControllerEndpointFacts), file+span = the action method declaration,
    /// Controller —Contains→ Endpoint, Endpoint —HandledBy→ action. Refusals are counted with a
    /// reason; a conventionally-routed controller (no route templates anywhere) is a different
    /// routing system — noted once per class, never counted as unresolved.
    ///
    /// <paramref name="syntacticOnlySignal"/> is non-null (v0.13.1) exactly when the containing
    /// class reached this method via the WIDENED syntactic prefilter (no base list, but looks
    /// controller-ish some other way) rather than the base-list one. If classification still
    /// fails specifically because <see cref="ControllerEndpointFacts.IsController"/> says no (not
    /// because the method itself isn't action-shaped), that gap is DISCLOSED — a counted category
    /// with a reason — never silently skipped: the whole point of this fix is that "looks like a
    /// controller but isn't recognized" must never again be invisible.
    /// </summary>
    private void HandleControllerAction(MethodDeclarationSyntax declaration, IMethodSymbol method, string? syntacticOnlySignal = null)
    {
        var classification = ControllerEndpointFacts.Classify(method);
        if (classification is null)
        {
            if (syntacticOnlySignal is not null
                && ControllerEndpointFacts.IsActionShaped(method)
                && !ControllerEndpointFacts.IsController(method.ContainingType)
                && _controllerLikeUnrecognized.Add(method.ContainingType))
            {
                _warnings.Add(
                    $"Class '{method.ContainingType.Name}' looks like a controller ({syntacticOnlySignal}) but was not "
                    + "recognized as one — it doesn't derive from ControllerBase and doesn't match ASP.NET's "
                    + "POCO-controller discovery rule (public, concrete, non-generic, name ending in \"Controller\" or "
                    + "[Controller]-attributed, not opted out via [NonController]) — its actions are not modeled as endpoints.");
            }

            return;
        }

        if (classification.IsConventionallyRouted)
        {
            if (_conventionalControllers.Add(method.ContainingType))
            {
                _warnings.Add(
                    $"Controller '{method.ContainingType.Name}' is conventionally routed (no route attributes) — "
                    + "its actions are not modeled as endpoints (attribute routing only).");
            }

            return;
        }

        foreach (string reason in classification.UnresolvedReasons)
        {
            _unresolvedEndpoints++;
            _warnings.Add($"Unresolved endpoint registration at {Location(declaration)}: {reason} (counted, not guessed).");
        }

        if (classification.Routes.Count == 0)
        {
            return;
        }

        var handlerNode = GetOrCreateNode(method);
        var controllerNode = GetOrCreateNode(method.ContainingType);
        var location = declaration.GetLocation();
        foreach (var (verb, template) in classification.Routes)
        {
            var node = SymbolNode.Create(
                NodeKind.Endpoint,
                name: template,
                fqn: $"{verb} {template}",
                filePath: location.SourceTree?.FilePath,
                span: new SourceSpan(location.SourceSpan.Start, location.SourceSpan.End));
            _nodes.Add(node);

            if (controllerNode is not null)
            {
                _edges.Add(new RelationshipEdge(controllerNode.Id, node.Id, RelationshipKind.Contains));
            }

            if (handlerNode is not null)
            {
                _edges.Add(new RelationshipEdge(node.Id, handlerNode.Id, RelationshipKind.HandledBy));
            }
        }
    }

    /// <summary>
    /// Razor Pages handler methods (v0.12.2, foreign-patterns-trial finding #2): no route
    /// extraction — a page's real route is its file location under <c>Pages/</c>, a build-time
    /// convention this tool cannot resolve from syntax/semantics alone. Disclosure only, noted
    /// once per class, mirroring <see cref="HandleControllerAction"/>'s conventionally-routed
    /// case exactly.
    /// </summary>
    private void HandleRazorPageHandler(IMethodSymbol method)
    {
        if (!RazorPageFacts.IsPageHandler(method))
        {
            return;
        }

        if (_razorPagesNotModeled.Add(method.ContainingType))
        {
            _warnings.Add(
                $"Page '{method.ContainingType.Name}' (Razor Pages) has handler methods (OnGet/OnPost/...) — "
                + "Razor Pages route by file location, not by attribute, so its handlers are not modeled as endpoints.");
        }
    }

    /// <summary>The nearest enclosing named type's node — for top-level statements, the synthesized Program class.</summary>
    private SymbolNode? GetEnclosingTypeNode(SyntaxNode syntax)
    {
        var symbol = _model.GetEnclosingSymbol(syntax.SpanStart, _cancellationToken);
        while (symbol is not null and not INamedTypeSymbol)
        {
            symbol = symbol.ContainingSymbol;
        }

        return symbol is INamedTypeSymbol type ? GetOrCreateNode(type) : null;
    }

    private static string Location(SyntaxNode syntax)
    {
        var span = syntax.GetLocation().GetLineSpan();
        return $"{span.Path}:{span.StartLinePosition.Line + 1}";
    }
}
