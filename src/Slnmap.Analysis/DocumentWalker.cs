using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Slnmap.Core.Analysis;
using Slnmap.Core.Graph;

namespace Slnmap.Analysis;

internal sealed record DocumentResult(
    IReadOnlyList<SymbolNode> Nodes,
    IReadOnlyList<RelationshipEdge> Edges,
    IReadOnlyList<string> Warnings)
{
    /// <summary>
    /// What this document's walk found but could not model. Counters are derived from these over
    /// the merged graph (never summed per run), so they survive incremental re-analysis.
    /// </summary>
    public IReadOnlyList<Disclosure> Disclosures { get; init; } = [];

    public IReadOnlyList<ExternalCall> ExternalCalls { get; init; } = [];

    public IReadOnlyList<DiRegistration> DiRegistrations { get; init; } = [];

    public IReadOnlyList<AttributeUsage> AttributeUsages { get; init; } = [];
}

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
    private readonly HashSet<INamedTypeSymbol> _routeAttributesUnresolved = new(SymbolEqualityComparer.Default);
    private readonly HashSet<INamedTypeSymbol> _razorPagesNotModeled = new(SymbolEqualityComparer.Default);
    private readonly HashSet<INamedTypeSymbol> _controllerLikeUnrecognized = new(SymbolEqualityComparer.Default);
    private readonly List<Disclosure> _disclosures = [];
    private readonly List<AttributeUsage> _attributeUsages = [];
    private readonly List<DiRegistration> _diRegistrations = [];
    private readonly Dictionary<(string Caller, string Target), ExternalCall> _externalCalls = [];
    private readonly Dictionary<IMethodSymbol, string> _externalTargetFqns = new(SymbolEqualityComparer.Default);
    private readonly string _projectName;
    private readonly string? _routePrefix;

    private DocumentWalker(SemanticModel model, string projectNodeId, string projectName, AnalysisOptions options, CancellationToken cancellationToken)
    {
        _model = model;
        _projectNodeId = projectNodeId;
        _projectName = projectName;
        _routePrefix = AnalysisOptions.NormalizeRoutePrefix(options.RoutePrefix);
        _cancellationToken = cancellationToken;
    }

    public static Task<DocumentResult?> AnalyzeAsync(Document document, string projectNodeId, CancellationToken cancellationToken) =>
        AnalyzeAsync(document, projectNodeId, AnalysisOptions.Default, cancellationToken);

    public static async Task<DocumentResult?> AnalyzeAsync(
        Document document, string projectNodeId, AnalysisOptions options, CancellationToken cancellationToken)
    {
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (model is null || root is null)
        {
            return null;
        }

        var walker = new DocumentWalker(model, projectNodeId, document.Project.Name, options, cancellationToken);
        walker.Visit(root);
        return new DocumentResult(walker._nodes, walker._edges, walker._warnings)
        {
            Disclosures = walker._disclosures,
            AttributeUsages = walker._attributeUsages,
            DiRegistrations = walker._diRegistrations,
            ExternalCalls = [.. walker._externalCalls.Values],
        };
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
                            HandleRazorPageHandler(method, methodSymbol);
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
                case AttributeSyntax attribute:
                    HandleAttribute(attribute);
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

        // A registered MVC model convention can rewrite route templates at application-model
        // build time (e.g. inject a base-path prefix, or slugify [controller]/[action] tokens)
        // invisibly to static analysis. v0.13.1 disclosed only IApplicationModelConvention
        // registrations (reports/v0130-regression-investigation-0of22-realworld.md); v0.14.0
        // (reports/gap-b-route-conventions-investigation.md) adds the controller- and
        // action-model overloads — eShopOnWeb's RouteTokenTransformerConvention went through one
        // undisclosed — and records each as a file-owned Disclosure so it reaches MCP results and
        // survives incremental runs. Recognized by the convention's real type, never by the
        // receiver's name; what the convention actually does is never interpreted.
        if (TryDescribeConventionRegistration(invocation, method) is { } convention)
        {
            Disclose(DisclosureKinds.RouteConvention, convention, invocation);
            _warnings.Add(
                $"Route convention {convention} registered at {Location(invocation)} can rewrite route templates at "
                + "runtime (e.g. inject a base-path prefix, or transform [controller]/[action] tokens) invisibly to "
                + "static analysis — extracted controller endpoint templates may not reflect what the app actually "
                + "serves. slnmap does not interpret convention implementations.");
        }

        // v0.14.0 (get_di_registrations): Microsoft.Extensions.DependencyInjection registration
        // calls are external, so they would die at the external-target early return below too.
        if (DiRegistrationFacts.TryRead(invocation, method, _model, _cancellationToken) is { } registration)
        {
            _diRegistrations.Add(new DiRegistration(
                registration.ServiceFqn,
                registration.ImplementationFqn,
                registration.Lifetime,
                registration.RegistrationKind,
                GetEnclosingMemberNode(invocation)?.Id,
                invocation.SyntaxTree.FilePath,
                invocation.SpanStart,
                _projectName));
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
            RecordExternalCall(invocation, method);
            return;
        }

        if (GetEnclosingMemberNode(invocation) is { } source)
        {
            _edges.Add(new RelationshipEdge(source.Id, target.Id, RelationshipKind.Calls));
        }
    }

    /// <summary>
    /// v0.14.0 (find_callers_of_external, docs/EXPANSION-SPECS.md §7): a call into a symbol
    /// outside the solution — a package or framework method — is recorded as a fact keyed by the
    /// target's FQN, namespace and assembly (never a node: there is nothing in source to point
    /// at). Attributed to the enclosing member, or the enclosing type for an initializer.
    /// </summary>
    private void RecordExternalCall(SyntaxNode site, IMethodSymbol method)
    {
        var original = method.OriginalDefinition;
        if (SymbolFacts.IsInSource(original) || original.ContainingType is not { } containingType)
        {
            return;
        }

        if ((GetEnclosingMemberNode(site) ?? GetEnclosingTypeNode(site)) is not { } caller)
        {
            return;
        }

        // Rendered once per target per document: a generated EF model snapshot repeats the same
        // few fluent-API methods thousands of times.
        if (!_externalTargetFqns.TryGetValue(original, out var targetFqn))
        {
            targetFqn = original.ToDisplayString(SymbolFacts.FqnFormat);
            _externalTargetFqns.Add(original, targetFqn);
        }

        if (_externalCalls.TryGetValue((caller.Id, targetFqn), out var existing))
        {
            // One fact per (caller, target): the first site plus a count (document order, so
            // "first" is the earliest in the file).
            _externalCalls[(caller.Id, targetFqn)] = existing with { CallCount = existing.CallCount + 1 };
            return;
        }

        _externalCalls[(caller.Id, targetFqn)] = new ExternalCall(
            caller.Id,
            targetFqn,
            containingType.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : string.Empty,
            original.ContainingAssembly?.Name,
            site.SyntaxTree.FilePath,
            site.SpanStart);
    }

    /// <summary>
    /// An attribute whose class implements a controller- or action-model convention is applied by
    /// MVC to the decorated controller/action at startup — the attribute form of the same
    /// route-rewriting hook <see cref="TryDescribeConventionRegistration"/> discloses for
    /// <c>Conventions.Add</c>. Disclosed the same way.
    /// </summary>
    private void HandleAttribute(AttributeSyntax attribute)
    {
        var resolved = _model.GetTypeInfo(attribute, _cancellationToken).Type as INamedTypeSymbol;
        RecordAttributeUsage(attribute, resolved);

        if (resolved is not { } type || ConventionInterfaceOf(type) is not { } iface)
        {
            return;
        }

        string convention = $"{TypeFqn(type)} ({iface}, applied as an attribute)";
        Disclose(DisclosureKinds.RouteConvention, convention, attribute);
        _warnings.Add(
            $"Route convention {convention} at {Location(attribute)} can rewrite route templates at runtime "
            + "invisibly to static analysis — extracted controller endpoint templates may not reflect what the app "
            + "actually serves. slnmap does not interpret convention implementations.");
    }

    /// <summary>
    /// v0.14.0 (get_attribute_usages, docs/EXPANSION-SPECS.md §10): records the attribute against
    /// the symbol it decorates. The attribute is stored by FQN as text — it is usually a framework
    /// type, never a node. Parameter, return-value, accessor and type-parameter attributes are
    /// attributed to their containing member/type; assembly- and module-level ones to the project.
    /// An attribute whose type does not resolve is still recorded, as "unresolved:{name as written}",
    /// rather than dropped.
    /// </summary>
    private void RecordAttributeUsage(AttributeSyntax attribute, INamedTypeSymbol? type)
    {
        string attributeFqn = type is { TypeKind: not TypeKind.Error }
            ? type.OriginalDefinition.ToDisplayString(SymbolFacts.FqnFormat)
            : "unresolved:" + attribute.Name.ToString();
        string file = attribute.SyntaxTree.FilePath;

        foreach (string targetId in AttributeTargets(attribute))
        {
            _attributeUsages.Add(new AttributeUsage(targetId, attributeFqn, file, attribute.SpanStart));
        }
    }

    /// <summary>Node ids of what an attribute decorates (several for a multi-declarator field).</summary>
    private IEnumerable<string> AttributeTargets(AttributeSyntax attribute)
    {
        if (attribute.Parent is not AttributeListSyntax list)
        {
            yield break;
        }

        if (list.Parent is CompilationUnitSyntax)
        {
            // [assembly: ...] / [module: ...]
            yield return _projectNodeId;
            yield break;
        }

        if (list.Parent is BaseFieldDeclarationSyntax field)
        {
            foreach (var declarator in field.Declaration.Variables)
            {
                if (GetOrCreateNode(_model.GetDeclaredSymbol(declarator, _cancellationToken)) is { } fieldNode)
                {
                    yield return fieldNode.Id;
                }
            }

            yield break;
        }

        // Walk up to the nearest declaration that is a node: a parameter, accessor, type parameter
        // or return-value attribute lands on the member or type that owns it.
        for (SyntaxNode? owner = list.Parent; owner is not null; owner = owner.Parent)
        {
            if (owner is MemberDeclarationSyntax or EnumMemberDeclarationSyntax or LocalFunctionStatementSyntax
                && _model.GetDeclaredSymbol(owner, _cancellationToken) is { } declared
                && GetOrCreateNode(declared) is { } node)
            {
                yield return node.Id;
                yield break;
            }

            if (owner is LambdaExpressionSyntax or AnonymousFunctionExpressionSyntax
                && GetEnclosingMemberNode(owner) is { } enclosing)
            {
                yield return enclosing.Id;
                yield break;
            }
        }

        // No owning declaration is a node (a local function in top-level statements, an operator,
        // a destructor): fall back to the enclosing type, then the project — never drop the usage
        // (v0.14.0 QA finding 8).
        yield return (GetEnclosingMemberNode(list) ?? GetEnclosingTypeNode(list))?.Id ?? _projectNodeId;
    }

    /// <summary>The MVC model-convention interfaces whose implementations can rewrite routes.</summary>
    private static readonly string[] RouteConventionInterfaces =
        ["IApplicationModelConvention", "IControllerModelConvention", "IActionModelConvention"];

    /// <summary>
    /// For a call registering an MVC model convention — <c>Add</c> or <c>Insert</c> whose LAST
    /// parameter is (or implements) <c>IApplicationModelConvention</c>, <c>IControllerModelConvention</c>
    /// or <c>IActionModelConvention</c> — describes it as <c>"{argument type FQN} ({interface})"</c>.
    /// Covers <c>MvcOptions.Conventions.Add/Insert(...)</c> and ASP.NET's
    /// <c>ApplicationModelConventionExtensions.Add(this IList&lt;IApplicationModelConvention&gt;, IControllerModelConvention/IActionModelConvention)</c>
    /// overloads alike, by the parameter's real type, never the receiver's name. Null otherwise.
    /// </summary>
    private string? TryDescribeConventionRegistration(InvocationExpressionSyntax invocation, IMethodSymbol method)
    {
        if (method.Name is not ("Add" or "Insert")
            || method.Parameters.Length is 0
            || method.Parameters[^1].Type is not INamedTypeSymbol parameterType
            || ConventionInterfaceOf(parameterType) is not { } parameterInterface
            || invocation.ArgumentList.Arguments.Count == 0)
        {
            return null;
        }

        // Prefer the argument's concrete type ("ApiRoutePrefixConvention"), which is what a reader
        // can go and look at; fall back to the parameter's type when the argument has none.
        var argument = invocation.ArgumentList.Arguments[^1].Expression;
        var argumentType = _model.GetTypeInfo(argument, _cancellationToken).Type as INamedTypeSymbol;
        var described = argumentType ?? parameterType;
        string iface = ConventionInterfaceOf(described) ?? parameterInterface;
        return $"{TypeFqn(described)} ({iface})";
    }

    /// <summary>
    /// The model-convention interface <paramref name="type"/> is or implements (application-level
    /// first), or null. Matched by name AND namespace, so a same-named user interface never counts.
    /// </summary>
    private static string? ConventionInterfaceOf(INamedTypeSymbol type)
    {
        foreach (string name in RouteConventionInterfaces)
        {
            if (IsConventionInterface(type, name) || type.AllInterfaces.Any(i => IsConventionInterface(i, name)))
            {
                return name;
            }
        }

        return null;
    }

    private static bool IsConventionInterface(INamedTypeSymbol type, string name) =>
        type is { TypeKind: TypeKind.Interface, ContainingNamespace: { } ns }
        && type.Name == name
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
            RecordExternalCall(creation, constructor);
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
            Disclose(DisclosureKinds.UnresolvedEndpoint, extraction.UnresolvedReason ?? "unresolved", invocation);
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
                Disclose(DisclosureKinds.ControllerLikeUnrecognized, TypeFqn(method.ContainingType), declaration);
                _warnings.Add(
                    $"Class '{method.ContainingType.Name}' looks like a controller ({syntacticOnlySignal}) but was not "
                    + "recognized as one — it doesn't derive from ControllerBase and doesn't match ASP.NET's "
                    + "POCO-controller discovery rule (public, concrete, non-generic, name ending in \"Controller\" or "
                    + "[Controller]-attributed, not opted out via [NonController]) — its actions are not modeled as endpoints.");
            }

            return;
        }

        if (classification.RouteAttributesUnresolved)
        {
            // Resolved refusals on the same action (an [HttpHead] beside an unresolved [HttpGet])
            // are still unmodeled routes: counted, not dropped (QA review of v0.14.1).
            DiscloseRefusals(classification, declaration);
            if (_routeAttributesUnresolved.Add(method.ContainingType))
            {
                Disclose(DisclosureKinds.RouteAttributesUnresolved, TypeFqn(method.ContainingType), declaration);
                _warnings.Add(
                    $"Controller '{method.ContainingType.Name}' has route attributes whose types don't resolve "
                    + "(is the project restored?) — its routes can't be read, so its actions are not modeled as endpoints.");
            }

            return;
        }

        if (classification.IsConventionallyRouted)
        {
            if (_conventionalControllers.Add(method.ContainingType))
            {
                Disclose(DisclosureKinds.ConventionalController, TypeFqn(method.ContainingType), declaration);
                _warnings.Add(
                    $"Controller '{method.ContainingType.Name}' is conventionally routed (no route attributes) — "
                    + "its actions are not modeled as endpoints (attribute routing only).");
            }

            return;
        }

        DiscloseRefusals(classification, declaration);

        if (classification.Routes.Count == 0)
        {
            return;
        }

        var handlerNode = GetOrCreateNode(method);
        var controllerNode = GetOrCreateNode(method.ContainingType);
        var location = declaration.GetLocation();
        int prefixSegments = _routePrefix is null ? 0 : _routePrefix.Trim('/').Split('/').Length;
        for (int routeIndex = 0; routeIndex < classification.Routes.Count; routeIndex++)
        {
            var (verb, declaredTemplate) = classification.Routes[routeIndex];
            // --route-prefix (v0.14.0): the user states the prefix a runtime convention adds;
            // the endpoint is marked so tool output never presents it as derived from code.
            string template = _routePrefix is null
                ? declaredTemplate
                : declaredTemplate.Trim('/').Length == 0 ? _routePrefix : _routePrefix + "/" + declaredTemplate.TrimStart('/');
            var node = SymbolNode.Create(
                NodeKind.Endpoint,
                name: template,
                fqn: $"{verb} {template}",
                filePath: location.SourceTree?.FilePath,
                span: new SourceSpan(location.SourceSpan.Start, location.SourceSpan.End));
            _nodes.Add(node);
            if (_routePrefix is not null)
            {
                Disclose(DisclosureKinds.RoutePrefixApplied, node.Fqn, declaration);
            }

            // Token provenance (v0.14.0, B4): lets matching tolerate a RouteTokenTransformerConvention's
            // rewrite of exactly these segments, and no literal one.
            if (classification.RouteTokenSegments is { } allTokenSegments
                && routeIndex < allTokenSegments.Count
                && allTokenSegments[routeIndex].Count > 0)
            {
                Disclose(
                    DisclosureKinds.TokenSegments,
                    DisclosureKinds.TokenSegmentsDetail(node.Fqn, allTokenSegments[routeIndex].Select(i => i + prefixSegments)),
                    declaration);
            }

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
    private void HandleRazorPageHandler(MethodDeclarationSyntax declaration, IMethodSymbol method)
    {
        if (!RazorPageFacts.IsPageHandler(method))
        {
            return;
        }

        if (_razorPagesNotModeled.Add(method.ContainingType))
        {
            Disclose(DisclosureKinds.RazorPageNotModeled, TypeFqn(method.ContainingType), declaration);
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

    /// <summary>
    /// Every refusal is one unmodeled route, so identical reasons on one action (two [HttpHead]
    /// attributes) count separately. They share the declaration's location, so the stored detail
    /// carries an occurrence number to keep the facts distinct (v0.14.1, QA #6); the detail is only
    /// ever counted, never shown.
    /// </summary>
    private void DiscloseRefusals(ControllerActionClassification classification, MethodDeclarationSyntax declaration)
    {
        var seenReasons = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string reason in classification.UnresolvedReasons)
        {
            int occurrence = seenReasons[reason] = seenReasons.GetValueOrDefault(reason) + 1;
            Disclose(DisclosureKinds.UnresolvedEndpoint, occurrence == 1 ? reason : $"{reason} (#{occurrence})", declaration);
            _warnings.Add($"Unresolved endpoint registration at {Location(declaration)}: {reason} (counted, not guessed).");
        }
    }

    private void Disclose(string kind, string detail, SyntaxNode at) =>
        _disclosures.Add(new Disclosure(kind, detail, at.SyntaxTree.FilePath, at.SpanStart));

    private static string TypeFqn(ITypeSymbol type) => type.OriginalDefinition.ToDisplayString(SymbolFacts.FqnFormat);

    private static string Location(SyntaxNode syntax)
    {
        var span = syntax.GetLocation().GetLineSpan();
        return $"{span.Path}:{span.StartLinePosition.Line + 1}";
    }
}
