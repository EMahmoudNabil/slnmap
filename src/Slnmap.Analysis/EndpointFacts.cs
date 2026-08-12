using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Slnmap.Analysis;

/// <summary>
/// The outcome of classifying one Map* invocation. <see cref="None"/> (null) means the call is
/// not an endpoint registration at all (an unrelated method that happens to be named Map*, or a
/// forwarder's own body — the registration lives at the forwarder's call sites). A non-null
/// result with a null <see cref="Template"/> is a real registration whose route could not be
/// resolved statically: the caller must count it, never guess it.
/// </summary>
internal sealed record EndpointExtraction(
    string Verb,
    string? Template,
    ExpressionSyntax? HandlerExpression,
    string? UnresolvedReason);

/// <summary>
/// Pure semantic-model queries for ASP.NET Core Minimal API endpoint extraction (the endpoint-nodes
/// investigation, reports/endpoint-nodes-investigation.md). Argument roles are decided by overload
/// resolution — the resolved method symbol's parameter types — never by argument position: the same
/// verb name is used both by the framework's <c>MapGet("pattern", handler)</c> and by
/// CleanArchitecture-style in-source extensions with the reversed <c>MapGet(handler, "pattern")</c>
/// order. Everything here is deterministic-or-declared: a registration either resolves through
/// compiler facts (constants, overload defaults, single-hop in-source forwarder folding, the
/// leaf-class-guarded <c>GetType().Name</c> prefix convention) or is reported unresolved with a
/// reason.
/// </summary>
internal static class EndpointFacts
{
    private const int MaxResolutionDepth = 8;
    private const int MaxReceiverHops = 16;

    /// <summary>
    /// Classifies an invocation already known to resolve to a method named Map{Get,Post,Put,Delete,Patch}.
    /// <paramref name="method"/> must be the symbol exactly as GetSymbolInfo returned it (reduced for
    /// extension-syntax calls), so its parameter list aligns with the syntactic argument list.
    /// </summary>
    public static EndpointExtraction? TryExtract(
        InvocationExpressionSyntax invocation,
        IMethodSymbol method,
        SemanticModel model,
        CancellationToken cancellationToken)
    {
        if (!IsVerbName(method.Name))
        {
            return null;
        }

        var original = (method.ReducedFrom ?? method).OriginalDefinition;

        if (IsFrameworkMapMethod(original))
        {
            return ExtractFromFrameworkCall(invocation, method, original, model, cancellationToken);
        }

        if (!SymbolFacts.IsInSource(original))
        {
            // Some other library's Map*-named method — not the minimal-API registration surface.
            return null;
        }

        return ExtractFromInSourceCall(invocation, method, original, model, cancellationToken);
    }

    private static EndpointExtraction? ExtractFromFrameworkCall(
        InvocationExpressionSyntax invocation,
        IMethodSymbol asResolved,
        IMethodSymbol original,
        SemanticModel model,
        CancellationToken cancellationToken)
    {
        string verb = VerbOf(original.Name);
        if (!TryFindPatternAndHandlerParameters(asResolved, out var patternParam, out var handlerParam))
        {
            return new EndpointExtraction(verb, null, null,
                $"unrecognized framework {original.Name} overload (no single string pattern + delegate handler parameter pair)");
        }

        var patternExpr = FindArgumentExpression(invocation, asResolved, patternParam.Name, out bool patternOmitted);
        var handlerExpr = FindArgumentExpression(invocation, asResolved, handlerParam.Name, out _);

        // A framework Map* call whose pattern argument is a parameter of the enclosing method is a
        // forwarder BODY, not a registration site. If the enclosing method is itself an extractable
        // Map* forwarder, its call sites carry the registrations — skip the body silently to avoid
        // double counting. Any other wrapper hides its call sites from the name prefilter entirely,
        // so the body is the one place the gap can be surfaced: count it.
        if (patternExpr is not null
            && model.GetSymbolInfo(patternExpr, cancellationToken).Symbol is IParameterSymbol enclosingParam
            && enclosingParam.ContainingSymbol is IMethodSymbol enclosingMethod)
        {
            if (IsVerbName(enclosingMethod.Name)
                && TryGetForwarderShape(enclosingMethod.OriginalDefinition, model.Compilation, cancellationToken) is not null)
            {
                return null;
            }

            return new EndpointExtraction(verb, null, null,
                $"route pattern is a parameter of the enclosing method '{enclosingMethod.Name}' — registrations routed through this wrapper are not statically resolvable");
        }

        string? pattern = patternOmitted
            ? DefaultPatternValue(patternParam)
            : patternExpr is null ? null : TryResolveRoutePattern(patternExpr, model, scope: null, MaxResolutionDepth, cancellationToken);
        if (pattern is null)
        {
            return new EndpointExtraction(verb, null, handlerExpr,
                $"route pattern does not resolve to a compile-time string ({Describe(patternExpr)})");
        }

        if (!TryResolveGroupPrefix(ReceiverOf(invocation, asResolved), model, scope: null, MaxReceiverHops, cancellationToken, out string? prefix, out string? prefixReason))
        {
            return new EndpointExtraction(verb, null, handlerExpr, prefixReason);
        }

        return new EndpointExtraction(verb, ComposeTemplate(prefix!, pattern), handlerExpr, null);
    }

    private static EndpointExtraction? ExtractFromInSourceCall(
        InvocationExpressionSyntax invocation,
        IMethodSymbol asResolved,
        IMethodSymbol original,
        SemanticModel model,
        CancellationToken cancellationToken)
    {
        if (DeclarationTreeOf(original) is not { } declarationTree || !model.Compilation.ContainsSyntaxTree(declarationTree))
        {
            // In-source but declared in another project: this compilation cannot inspect the body,
            // so the forwarder cannot be verified. Declared, not guessed.
            return new EndpointExtraction(VerbOf(original.Name), null, null,
                $"'{original.Name}' is declared in another project — its body cannot be verified as a single-hop forwarder from this compilation");
        }

        var shape = TryGetForwarderShape(original, model.Compilation, cancellationToken);
        if (shape is null)
        {
            // An in-source Map*-named method that provably does not forward to a framework Map* —
            // not an endpoint registration.
            return null;
        }

        string verb = VerbOf(shape.FrameworkMethod.Name);
        var patternExpr = FindArgumentExpression(invocation, asResolved, shape.PatternParameter.Name, out bool patternOmitted);
        var handlerExpr = FindArgumentExpression(invocation, asResolved, shape.HandlerParameter.Name, out _);

        string? pattern = patternOmitted
            ? DefaultPatternValue(shape.PatternParameter)
            : patternExpr is null ? null : TryResolveRoutePattern(patternExpr, model, scope: null, MaxResolutionDepth, cancellationToken);
        if (pattern is null)
        {
            return new EndpointExtraction(verb, null, handlerExpr,
                $"route pattern does not resolve to a compile-time string ({Describe(patternExpr)})");
        }

        if (!TryResolveGroupPrefix(ReceiverOf(invocation, asResolved), model, scope: null, MaxReceiverHops, cancellationToken, out string? prefix, out string? prefixReason))
        {
            return new EndpointExtraction(verb, null, handlerExpr, prefixReason);
        }

        return new EndpointExtraction(verb, ComposeTemplate(prefix!, pattern), handlerExpr, null);
    }

    // ---- Map* classification helpers ------------------------------------------------------------

    private static bool IsVerbName(string name) =>
        name is "MapGet" or "MapPost" or "MapPut" or "MapDelete" or "MapPatch";

    private static string VerbOf(string mapMethodName) => mapMethodName["Map".Length..].ToUpperInvariant();

    private static bool IsFrameworkMapMethod(IMethodSymbol original) =>
        !SymbolFacts.IsInSource(original)
        && original.ContainingType is { Name: "EndpointRouteBuilderExtensions" } type
        && type.ContainingNamespace?.ToDisplayString() == "Microsoft.AspNetCore.Builder";

    private sealed record ForwarderShape(
        IParameterSymbol PatternParameter,
        IParameterSymbol HandlerParameter,
        IMethodSymbol FrameworkMethod);

    /// <summary>
    /// Verifies the single-hop forwarder shape: an in-source Map*-named method whose body invokes a
    /// framework Map* passing its own string parameter as the pattern and its own delegate parameter
    /// as the handler (the CleanArchitecture reversed-argument extension). Returns the two OUTER
    /// parameters (on <paramref name="original"/>, un-reduced) plus the inner framework method —
    /// the verb of record. Anything that does not match exactly is not folded.
    /// </summary>
    private static ForwarderShape? TryGetForwarderShape(
        IMethodSymbol original,
        Compilation compilation,
        CancellationToken cancellationToken)
    {
        if (!IsVerbName(original.Name) || !SymbolFacts.IsInSource(original))
        {
            return null;
        }

        if (DeclarationTreeOf(original) is not { } tree
            || !compilation.ContainsSyntaxTree(tree)
            || original.DeclaringSyntaxReferences[0].GetSyntax(cancellationToken) is not MethodDeclarationSyntax declaration)
        {
            return null;
        }

        SyntaxNode? body = declaration.Body ?? (SyntaxNode?)declaration.ExpressionBody;
        if (body is null)
        {
            return null;
        }

        var forwarderModel = compilation.GetSemanticModel(tree);
        foreach (var inner in body.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (forwarderModel.GetSymbolInfo(inner, cancellationToken).Symbol is not IMethodSymbol innerMethod)
            {
                continue;
            }

            var innerOriginal = (innerMethod.ReducedFrom ?? innerMethod).OriginalDefinition;
            if (!IsVerbName(innerOriginal.Name) || !IsFrameworkMapMethod(innerOriginal))
            {
                continue;
            }

            if (!TryFindPatternAndHandlerParameters(innerMethod, out var innerPattern, out var innerHandler))
            {
                continue;
            }

            if (FindArgumentExpression(inner, innerMethod, innerPattern.Name, out _) is not { } innerPatternExpr
                || FindArgumentExpression(inner, innerMethod, innerHandler.Name, out _) is not { } innerHandlerExpr)
            {
                continue;
            }

            if (forwarderModel.GetSymbolInfo(innerPatternExpr, cancellationToken).Symbol is not IParameterSymbol outerPattern
                || forwarderModel.GetSymbolInfo(innerHandlerExpr, cancellationToken).Symbol is not IParameterSymbol outerHandler
                || !SymbolEqualityComparer.Default.Equals(outerPattern.ContainingSymbol, original)
                || !SymbolEqualityComparer.Default.Equals(outerHandler.ContainingSymbol, original))
            {
                continue;
            }

            return new ForwarderShape(outerPattern, outerHandler, innerOriginal);
        }

        return null;
    }

    /// <summary>
    /// Locates the route-pattern parameter (the unique string) and the handler parameter (the unique
    /// System.Delegate or delegate-typed one) on the method form whose parameter list aligns with the
    /// call syntax. Type-based, because parameter names are a convention, not a contract.
    /// </summary>
    private static bool TryFindPatternAndHandlerParameters(
        IMethodSymbol method,
        out IParameterSymbol patternParameter,
        out IParameterSymbol handlerParameter)
    {
        patternParameter = null!;
        handlerParameter = null!;
        IParameterSymbol? pattern = null, handler = null;
        foreach (var parameter in method.Parameters)
        {
            if (parameter.Type.SpecialType == SpecialType.System_String)
            {
                if (pattern is not null)
                {
                    return false;
                }

                pattern = parameter;
            }
            else if (IsDelegateLike(parameter.Type))
            {
                if (handler is not null)
                {
                    return false;
                }

                handler = parameter;
            }
        }

        if (pattern is null || handler is null)
        {
            return false;
        }

        patternParameter = pattern;
        handlerParameter = handler;
        return true;
    }

    private static bool IsDelegateLike(ITypeSymbol type) =>
        type.TypeKind == TypeKind.Delegate
        || (type is { Name: "Delegate" or "MulticastDelegate" }
            && type.ContainingNamespace is { Name: "System", ContainingNamespace.IsGlobalNamespace: true });

    private static string? DefaultPatternValue(IParameterSymbol parameter) =>
        parameter.HasExplicitDefaultValue && parameter.ExplicitDefaultValue is string s ? s : null;

    private static SyntaxTree? DeclarationTreeOf(IMethodSymbol method) =>
        method.DeclaringSyntaxReferences.Length > 0 ? method.DeclaringSyntaxReferences[0].SyntaxTree : null;

    /// <summary>
    /// Maps a parameter name to the argument expression supplied for it at the call site, honoring
    /// named arguments; <paramref name="omitted"/> reports an optional parameter with no argument.
    /// <paramref name="asResolved"/> must be the form whose parameters align with the argument list.
    /// </summary>
    private static ExpressionSyntax? FindArgumentExpression(
        InvocationExpressionSyntax invocation,
        IMethodSymbol asResolved,
        string parameterName,
        out bool omitted)
    {
        omitted = false;
        var arguments = invocation.ArgumentList.Arguments;
        for (int i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i];
            if (argument.NameColon is { } nameColon)
            {
                if (nameColon.Name.Identifier.ValueText == parameterName)
                {
                    return argument.Expression;
                }
            }
            else if (i < asResolved.Parameters.Length && asResolved.Parameters[i].Name == parameterName)
            {
                return argument.Expression;
            }
        }

        omitted = true;
        return null;
    }

    /// <summary>The receiver expression carrying a possible group prefix: the extension-syntax receiver, or the first argument of a static-form call.</summary>
    private static ExpressionSyntax? ReceiverOf(InvocationExpressionSyntax invocation, IMethodSymbol asResolved)
    {
        if (invocation.Expression is MemberAccessExpressionSyntax memberAccess && asResolved.ReducedFrom is not null)
        {
            return memberAccess.Expression;
        }

        // Static-form invocation of the extension: the builder is the first argument.
        return asResolved.IsExtensionMethod && invocation.ArgumentList.Arguments.Count > 0
            ? invocation.ArgumentList.Arguments[0].Expression
            : null;
    }

    // ---- route pattern resolution ----------------------------------------------------------------

    /// <summary>
    /// A parameter-substitution scope for single-hop forwarder folding: maps the forwarder's own
    /// parameters back to the caller-side argument expressions (with the caller's semantic model),
    /// or to the overload's default value when the argument was omitted.
    /// </summary>
    private sealed class BindingScope
    {
        private readonly Dictionary<IParameterSymbol, (ExpressionSyntax? Expression, SemanticModel? Model, string? Constant)> _bindings =
            new(SymbolEqualityComparer.Default);

        public void Bind(IParameterSymbol parameter, ExpressionSyntax expression, SemanticModel model) =>
            _bindings[parameter] = (expression, model, null);

        public void BindConstant(IParameterSymbol parameter, string value) =>
            _bindings[parameter] = (null, null, value);

        public bool TryGet(IParameterSymbol parameter, out ExpressionSyntax? expression, out SemanticModel? model, out string? constant)
        {
            if (_bindings.TryGetValue(parameter, out var binding))
            {
                (expression, model, constant) = binding;
                return true;
            }

            (expression, model, constant) = (null, null, null);
            return false;
        }
    }

    /// <summary>
    /// Resolves a route-pattern expression to its compile-time string, or null. Supported:
    /// compile-time constants (literals, const fields, concatenations), <c>string.Empty</c>,
    /// single-initializer locals, forwarder parameters via <paramref name="scope"/>, and
    /// interpolated strings whose every hole resolves — including the guarded
    /// <c>receiver.GetType().Name</c> convention.
    /// </summary>
    private static string? TryResolveRoutePattern(
        ExpressionSyntax expression,
        SemanticModel model,
        BindingScope? scope,
        int depth,
        CancellationToken cancellationToken)
    {
        if (depth <= 0)
        {
            return null;
        }

        expression = Unwrap(expression);

        var constant = model.GetConstantValue(expression, cancellationToken);
        if (constant.HasValue && constant.Value is string constantString)
        {
            return constantString;
        }

        // The guarded GetType().Name convention — checked here (not only at interpolation holes)
        // because it typically arrives via a local: `var groupName = group.GetType().Name;`.
        if (TryResolveGetTypeName(expression, model, scope, cancellationToken, out string? typeName))
        {
            return typeName;
        }

        var symbol = model.GetSymbolInfo(expression, cancellationToken).Symbol;

        if (symbol is IFieldSymbol { Name: nameof(string.Empty), ContainingType.SpecialType: SpecialType.System_String })
        {
            return string.Empty;
        }

        if (symbol is ILocalSymbol local
            && local.DeclaringSyntaxReferences.Length == 1
            && local.DeclaringSyntaxReferences[0].GetSyntax(cancellationToken) is VariableDeclaratorSyntax { Initializer.Value: { } initializer })
        {
            return TryResolveRoutePattern(initializer, model, scope, depth - 1, cancellationToken);
        }

        if (symbol is IParameterSymbol parameter && scope is not null
            && scope.TryGet(parameter, out var callerExpr, out var callerModel, out var callerConstant))
        {
            if (callerConstant is not null)
            {
                return callerConstant;
            }

            // Single hop by design: the caller side resolves without a further scope.
            return callerExpr is not null && callerModel is not null
                ? TryResolveRoutePattern(callerExpr, callerModel, scope: null, depth - 1, cancellationToken)
                : null;
        }

        if (expression is InterpolatedStringExpressionSyntax interpolated)
        {
            var builder = new System.Text.StringBuilder();
            foreach (var content in interpolated.Contents)
            {
                switch (content)
                {
                    case InterpolatedStringTextSyntax text:
                        builder.Append(text.TextToken.ValueText);
                        break;
                    case InterpolationSyntax { AlignmentClause: null, FormatClause: null } hole:
                        string? value = TryResolveRoutePattern(hole.Expression, model, scope, depth - 1, cancellationToken);
                        if (value is null)
                        {
                            return null;
                        }

                        builder.Append(value);
                        break;
                    default:
                        return null;
                }
            }

            return builder.ToString();
        }

        return null;
    }

    /// <summary>
    /// The CleanArchitecture prefix convention: a hole of the shape <c>x.GetType().Name</c>.
    /// <c>GetType()</c> returns the RUNTIME type, so folding it to the receiver's static type name
    /// is sound only when no type in this compilation derives from that static type (sealed, or a
    /// verified leaf). Any other receiver refuses — the registration is counted, never guessed.
    /// When <c>x</c> is a forwarder parameter, the guard applies to the CALLER argument's static
    /// type (the parameter's own declared type is typically the abstract group base).
    /// </summary>
    private static bool TryResolveGetTypeName(
        ExpressionSyntax expression,
        SemanticModel model,
        BindingScope? scope,
        CancellationToken cancellationToken,
        out string? typeName)
    {
        typeName = null;
        if (expression is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Name" } nameAccess
            || Unwrap(nameAccess.Expression) is not InvocationExpressionSyntax getTypeCall
            || model.GetSymbolInfo(getTypeCall, cancellationToken).Symbol is not IMethodSymbol { Name: "GetType", Parameters.Length: 0 })
        {
            return false;
        }

        // Receiver of GetType(): explicit (x.GetType()) or implicit this (GetType()).
        ExpressionSyntax? receiver = getTypeCall.Expression is MemberAccessExpressionSyntax getTypeAccess
            ? Unwrap(getTypeAccess.Expression)
            : null;

        ITypeSymbol? staticType;
        SemanticModel guardModel = model;
        if (receiver is null)
        {
            staticType = model.GetEnclosingSymbol(expression.SpanStart, cancellationToken)?.ContainingType;
        }
        else if (model.GetSymbolInfo(receiver, cancellationToken).Symbol is IParameterSymbol parameter
            && scope is not null
            && scope.TryGet(parameter, out var callerExpr, out var callerModel, out _)
            && callerExpr is not null && callerModel is not null)
        {
            staticType = callerModel.GetTypeInfo(callerExpr, cancellationToken).Type;
            guardModel = callerModel;
        }
        else
        {
            staticType = model.GetTypeInfo(receiver, cancellationToken).Type;
        }

        if (staticType is not INamedTypeSymbol { TypeKind: TypeKind.Class } namedType
            || !IsLeafInCompilation(namedType, guardModel.Compilation, cancellationToken))
        {
            // Matched the convention but the guard refused: report "false" so the caller falls
            // through to the generic resolvers, which will fail and count the registration.
            return false;
        }

        typeName = namedType.Name;
        return true;
    }

    /// <summary>
    /// True when no type in this compilation's source assembly derives from <paramref name="type"/>
    /// — then (and only then) <c>GetType().Name</c> on a receiver of that static type equals the
    /// type's declared name. Sealed classes are leaves by definition.
    /// </summary>
    private static bool IsLeafInCompilation(INamedTypeSymbol type, Compilation compilation, CancellationToken cancellationToken)
    {
        if (type.IsSealed)
        {
            return true;
        }

        var stack = new Stack<INamespaceOrTypeSymbol>();
        stack.Push(compilation.Assembly.GlobalNamespace);
        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var member in stack.Pop().GetMembers())
            {
                if (member is INamespaceSymbol ns)
                {
                    stack.Push(ns);
                }
                else if (member is INamedTypeSymbol candidate)
                {
                    for (var baseType = candidate.BaseType; baseType is not null; baseType = baseType.BaseType)
                    {
                        if (SymbolEqualityComparer.Default.Equals(baseType.OriginalDefinition, type.OriginalDefinition))
                        {
                            return false;
                        }
                    }

                    stack.Push(candidate);
                }
            }
        }

        return true;
    }

    // ---- group prefix resolution -----------------------------------------------------------------

    /// <summary>
    /// Traces the receiver of a Map* call back to its route-group prefix. Follows locals to their
    /// initializers, fluent convention chains back to their receivers, framework MapGroup calls
    /// (recursing for nested groups), and single-hop in-source MapGroup forwarders (the guarded
    /// convention). Returns false — with a reason — whenever a receiver that could carry a prefix
    /// cannot be traced: count, never guess.
    /// </summary>
    private static bool TryResolveGroupPrefix(
        ExpressionSyntax? receiver,
        SemanticModel model,
        BindingScope? scope,
        int hops,
        CancellationToken cancellationToken,
        out string? prefix,
        out string? reason)
    {
        prefix = null;
        reason = null;
        if (receiver is null)
        {
            prefix = string.Empty;
            return true;
        }

        var expression = Unwrap(receiver);
        for (int hop = 0; hop < hops; hop++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            expression = Unwrap(expression);

            if (expression is InvocationExpressionSyntax invocation
                && model.GetSymbolInfo(invocation, cancellationToken).Symbol is IMethodSymbol method)
            {
                var original = (method.ReducedFrom ?? method).OriginalDefinition;

                if (original.Name == "MapGroup" && IsFrameworkMapMethod(original))
                {
                    return ResolveFrameworkMapGroup(invocation, method, model, scope, hops - hop - 1, cancellationToken, out prefix, out reason);
                }

                if (original.Name == "MapGroup" && SymbolFacts.IsInSource(original))
                {
                    return ResolveMapGroupForwarder(invocation, method, original, model, hops - hop - 1, cancellationToken, out prefix, out reason);
                }

                // Fluent convention chain (WithTags(...).RequireRateLimiting(...)): an extension
                // whose constructed return type is exactly its receiver's type returns the same
                // builder — step through to the receiver. Anything else is not a pass-through.
                if (method.ReducedFrom is not null
                    && invocation.Expression is MemberAccessExpressionSyntax chainAccess
                    && SymbolEqualityComparer.Default.Equals(method.ReturnType, method.ReceiverType))
                {
                    expression = chainAccess.Expression;
                    continue;
                }

                return Terminal(invocation, model, cancellationToken, out prefix, out reason);
            }

            var symbol = model.GetSymbolInfo(expression, cancellationToken).Symbol;
            if (symbol is ILocalSymbol local
                && local.DeclaringSyntaxReferences.Length == 1
                && local.DeclaringSyntaxReferences[0].GetSyntax(cancellationToken) is VariableDeclaratorSyntax { Initializer.Value: { } initializer })
            {
                expression = initializer;
                continue;
            }

            if (symbol is IParameterSymbol parameter && scope is not null
                && scope.TryGet(parameter, out var callerExpr, out var callerModel, out _)
                && callerExpr is not null && callerModel is not null)
            {
                // Cross back into the caller: from here on the caller's model governs, single hop.
                return TryResolveGroupPrefix(callerExpr, callerModel, scope: null, hops - hop - 1, cancellationToken, out prefix, out reason);
            }

            return Terminal(expression, model, cancellationToken, out prefix, out reason);
        }

        reason = "group receiver chain exceeds the traceable depth";
        return false;
    }

    private static bool ResolveFrameworkMapGroup(
        InvocationExpressionSyntax invocation,
        IMethodSymbol asResolved,
        SemanticModel model,
        BindingScope? scope,
        int hops,
        CancellationToken cancellationToken,
        out string? prefix,
        out string? reason)
    {
        prefix = null;
        reason = null;

        var patternParam = asResolved.Parameters.FirstOrDefault(p => p.Type.SpecialType == SpecialType.System_String);
        if (patternParam is null)
        {
            reason = "MapGroup overload takes a non-string route pattern";
            return false;
        }

        var patternExpr = FindArgumentExpression(invocation, asResolved, patternParam.Name, out bool omitted);
        string? segment = omitted
            ? DefaultPatternValue(patternParam)
            : patternExpr is null ? null : TryResolveRoutePattern(patternExpr, model, scope, MaxResolutionDepth, cancellationToken);
        if (segment is null)
        {
            reason = $"group prefix does not resolve to a compile-time string ({Describe(patternExpr)})";
            return false;
        }

        if (!TryResolveGroupPrefix(ReceiverOf(invocation, asResolved), model, scope, hops, cancellationToken, out string? parent, out reason))
        {
            return false;
        }

        prefix = CombinePrefix(parent!, segment);
        return true;
    }

    /// <summary>
    /// Folds a single-hop in-source MapGroup forwarder (the CleanArchitecture convention:
    /// <c>MapGroup(this WebApplication app, EndpointGroupBase group)</c> returning
    /// <c>app.MapGroup($"/api/{group.GetType().Name}")</c>). The forwarder's parameters are bound to
    /// the caller's argument expressions so the interpolation and the inner receiver resolve — and
    /// the <c>GetType().Name</c> leaf guard applies to the caller's static argument type.
    /// </summary>
    private static bool ResolveMapGroupForwarder(
        InvocationExpressionSyntax invocation,
        IMethodSymbol asResolved,
        IMethodSymbol original,
        SemanticModel model,
        int hops,
        CancellationToken cancellationToken,
        out string? prefix,
        out string? reason)
    {
        prefix = null;
        reason = null;

        if (DeclarationTreeOf(original) is not { } tree || !model.Compilation.ContainsSyntaxTree(tree))
        {
            reason = $"'{original.Name}' is declared in another project — its body cannot be folded from this compilation";
            return false;
        }

        if (original.DeclaringSyntaxReferences[0].GetSyntax(cancellationToken) is not MethodDeclarationSyntax declaration
            || (declaration.Body ?? (SyntaxNode?)declaration.ExpressionBody) is not { } body)
        {
            reason = $"'{original.Name}' has no inspectable body";
            return false;
        }

        // Bind the forwarder's parameters to the caller's arguments (receiver included for the
        // extension this-parameter, so the inner builder receiver can cross back to the caller).
        var scope = new BindingScope();
        var parameters = original.Parameters;
        if (asResolved.ReducedFrom is not null && invocation.Expression is MemberAccessExpressionSyntax callerAccess && parameters.Length > 0)
        {
            scope.Bind(parameters[0], callerAccess.Expression, model);
        }

        foreach (var parameter in parameters)
        {
            string name = parameter.Name;
            if (FindArgumentExpression(invocation, asResolved, name, out bool omitted) is { } argumentExpr)
            {
                scope.Bind(parameter, argumentExpr, model);
            }
            else if (omitted && parameter.HasExplicitDefaultValue && parameter.ExplicitDefaultValue is string defaultValue)
            {
                scope.BindConstant(parameter, defaultValue);
            }
        }

        var forwarderModel = model.Compilation.GetSemanticModel(tree);
        foreach (var inner in body.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (forwarderModel.GetSymbolInfo(inner, cancellationToken).Symbol is not IMethodSymbol innerMethod)
            {
                continue;
            }

            var innerOriginal = (innerMethod.ReducedFrom ?? innerMethod).OriginalDefinition;
            if (innerOriginal.Name != "MapGroup" || !IsFrameworkMapMethod(innerOriginal))
            {
                continue;
            }

            return ResolveFrameworkMapGroup(inner, innerMethod, forwarderModel, scope, hops, cancellationToken, out prefix, out reason);
        }

        reason = $"'{original.Name}' does not forward to the framework MapGroup in a single hop";
        return false;
    }

    /// <summary>
    /// End of the receiver chain: a RouteGroupBuilder we failed to trace (or the ambiguous
    /// IEndpointRouteBuilder interface, which may hold a group at runtime) is refused; any other
    /// concrete builder type (WebApplication, ...) carries no prefix.
    /// </summary>
    private static bool Terminal(
        ExpressionSyntax expression,
        SemanticModel model,
        CancellationToken cancellationToken,
        out string? prefix,
        out string? reason)
    {
        prefix = null;
        reason = null;
        var type = model.GetTypeInfo(expression, cancellationToken).Type;
        if (type is { Name: "RouteGroupBuilder" } && type.ContainingNamespace?.ToDisplayString() == "Microsoft.AspNetCore.Routing")
        {
            reason = "receiver is a route group whose MapGroup origin cannot be traced statically";
            return false;
        }

        if (type is { TypeKind: TypeKind.Interface, Name: "IEndpointRouteBuilder" })
        {
            reason = "receiver is an IEndpointRouteBuilder parameter — it may carry a group prefix at runtime that cannot be seen statically";
            return false;
        }

        prefix = string.Empty;
        return true;
    }

    // ---- composition -------------------------------------------------------------------------------

    /// <summary>Prefix of a nested group: parent prefix + this group's segment (no trailing-slash trim yet).</summary>
    private static string CombinePrefix(string parent, string segment) =>
        parent.Length == 0 ? segment : parent + "/" + segment;

    /// <summary>
    /// Composes the effective route template: prefix + "/" + pattern, duplicate slashes collapsed,
    /// a single trailing slash trimmed, always rooted; the bare root route stays "/".
    /// </summary>
    internal static string ComposeTemplate(string prefix, string pattern)
    {
        string combined = "/" + prefix + "/" + pattern;
        var builder = new System.Text.StringBuilder(combined.Length);
        foreach (char c in combined)
        {
            if (c == '/' && builder.Length > 0 && builder[^1] == '/')
            {
                continue;
            }

            builder.Append(c);
        }

        if (builder.Length > 1 && builder[^1] == '/')
        {
            builder.Length--;
        }

        return builder.ToString();
    }

    private static ExpressionSyntax Unwrap(ExpressionSyntax expression)
    {
        while (true)
        {
            switch (expression)
            {
                case ParenthesizedExpressionSyntax parenthesized:
                    expression = parenthesized.Expression;
                    break;
                case CastExpressionSyntax cast:
                    expression = cast.Expression;
                    break;
                default:
                    return expression;
            }
        }
    }

    private static string Describe(ExpressionSyntax? expression) =>
        expression is null ? "no pattern argument" : $"'{Truncate(expression.ToString(), 60)}'";

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";
}
