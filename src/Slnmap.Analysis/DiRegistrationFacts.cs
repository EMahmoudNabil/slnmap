using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Slnmap.Core.Graph;

namespace Slnmap.Analysis;

/// <summary>What one DI registration call registers, before it is tied to a file and caller.</summary>
internal sealed record DiRegistrationShape(string ServiceFqn, string? ImplementationFqn, string Lifetime, string RegistrationKind);

/// <summary>
/// v0.14.0 (get_di_registrations, docs/EXPANSION-SPECS.md §8): recognizes
/// <c>Microsoft.Extensions.DependencyInjection</c> registration calls and reads what they register
/// from the semantic model — generic type arguments, <c>typeof(...)</c> arguments, a factory
/// lambda's <c>new X(...)</c>, an instance argument's static type. Deterministic or declared: a
/// registration call whose service type cannot be read is still returned, as
/// <see cref="DiRegistrationKinds.Unrecognized"/>, never guessed and never dropped.
/// Registrations made inside library extension methods (AddControllers, AddMediatR, Scrutor's
/// Scan, assembly scanning) are not expanded — those bodies are not in source.
/// </summary>
internal static class DiRegistrationFacts
{
    private const string DiNamespace = "Microsoft.Extensions.DependencyInjection";

    /// <summary>Returns the shape when <paramref name="method"/> is a recognized registration call, else null.</summary>
    public static DiRegistrationShape? TryRead(InvocationExpressionSyntax invocation, IMethodSymbol method, SemanticModel model, CancellationToken cancellationToken)
    {
        if (!IsDiExtension(method))
        {
            return null;
        }

        string name = method.Name;
        if (name is "AddHostedService" && method.TypeArguments.Length == 1)
        {
            return new DiRegistrationShape("Microsoft.Extensions.Hosting.IHostedService", Fqn(method.TypeArguments[0]), "Singleton", DiRegistrationKinds.Generic);
        }

        if (name is "AddDbContext" or "AddDbContextPool" or "AddDbContextFactory" or "AddPooledDbContextFactory"
            && method.TypeArguments.Length >= 1)
        {
            // AddDbContext's lifetime is a parameter (default Scoped); a non-default one is not
            // folded here — the "(default)" suffix says what was assumed. For the factory forms
            // the context is the FIRST type argument (<TContext, TFactory>); for AddDbContext
            // <TContextService, TContextImplementation> the service is first, the implementation last.
            if (name.Contains("Factory", StringComparison.Ordinal))
            {
                string context = Fqn(method.TypeArguments[0]);
                return new DiRegistrationShape(
                    $"Microsoft.EntityFrameworkCore.IDbContextFactory<{context}>",
                    method.TypeArguments.Length > 1 ? Fqn(method.TypeArguments[1]) : null,
                    "Singleton (default)",
                    method.TypeArguments.Length > 1 ? DiRegistrationKinds.Generic : DiRegistrationKinds.Factory);
            }

            return new DiRegistrationShape(Fqn(method.TypeArguments[0]), Fqn(method.TypeArguments[^1]), "Scoped (default)", DiRegistrationKinds.Generic);
        }

        // ServiceDescriptor-based forms (Add, TryAdd, TryAddEnumerable, Replace): the lifetime
        // and types live inside a descriptor expression this reader does not evaluate — recorded
        // as unrecognized, never dropped (v0.14.0 QA finding 4).
        if (name is "Add" or "TryAdd" or "TryAddEnumerable" or "Replace" && IsServiceCollectionReceiver(method))
        {
            return new DiRegistrationShape(Abbreviate(invocation.ToString()), null, "Unknown", DiRegistrationKinds.Unrecognized);
        }

        if (LifetimeOf(name) is not { } baseLifetime)
        {
            return null;
        }

        // A keyed registration shows its key, so it never looks identical to the unkeyed one.
        string lifetime = KeyOf(method, invocation.ArgumentList.Arguments) is { } key
            ? $"{baseLifetime}, key {key}"
            : baseLifetime;

        var arguments = invocation.ArgumentList.Arguments;

        // Generic forms: Add{Lifetime}<TService, TImpl>(), Add{Lifetime}<T>(), <T>(factory), <T>(instance).
        if (method.TypeArguments.Length == 2)
        {
            return new DiRegistrationShape(Fqn(method.TypeArguments[0]), Fqn(method.TypeArguments[1]), lifetime, DiRegistrationKinds.Generic);
        }

        if (method.TypeArguments.Length == 1)
        {
            string service = Fqn(method.TypeArguments[0]);
            var payload = NonKeyArguments(method, arguments).LastOrDefault();
            if (payload is null)
            {
                return new DiRegistrationShape(service, service, lifetime, DiRegistrationKinds.Generic);
            }

            return IsFactoryArgument(method, arguments, payload)
                ? new DiRegistrationShape(service, FactoryCreatedType(payload.Expression, model, cancellationToken), lifetime, DiRegistrationKinds.Factory)
                : new DiRegistrationShape(service, StaticTypeOf(payload.Expression, model, cancellationToken), lifetime, DiRegistrationKinds.Instance);
        }

        // Non-generic forms: (typeof(I), typeof(T)), (typeof(T)), (typeof(I), factory), (typeof(I), instance).
        var typeArguments = NonKeyArguments(method, arguments)
            .Select(a => (Argument: a, Type: TypeOfArgument(a.Expression, model, cancellationToken)))
            .ToList();
        var typeOfs = typeArguments.Where(t => t.Type is not null).Select(t => t.Type!).ToList();
        bool everyTypeParameterIsTypeOf = method.Parameters
            .Where(p => p.Type is { Name: "Type", ContainingNamespace.Name: "System" })
            .Count() == typeOfs.Count;

        if (typeOfs.Count == 0 || !everyTypeParameterIsTypeOf)
        {
            // A Type held in a variable (a registration loop, a scanning helper): not readable.
            return new DiRegistrationShape(Abbreviate(invocation.ToString()), null, lifetime, DiRegistrationKinds.Unrecognized);
        }

        string kind = typeOfs.Any(IsOpenGeneric) ? DiRegistrationKinds.OpenGeneric : DiRegistrationKinds.TypeOf;
        string serviceFqn = Fqn(typeOfs[0]);
        if (typeOfs.Count >= 2)
        {
            return new DiRegistrationShape(serviceFqn, Fqn(typeOfs[1]), lifetime, kind);
        }

        var rest = typeArguments.Where(t => t.Type is null).Select(t => t.Argument).LastOrDefault();
        if (rest is null)
        {
            return new DiRegistrationShape(serviceFqn, serviceFqn, lifetime, kind);
        }

        return IsFactoryArgument(method, arguments, rest)
            ? new DiRegistrationShape(serviceFqn, FactoryCreatedType(rest.Expression, model, cancellationToken), lifetime, DiRegistrationKinds.Factory)
            : new DiRegistrationShape(serviceFqn, StaticTypeOf(rest.Expression, model, cancellationToken), lifetime, DiRegistrationKinds.Instance);
    }

    /// <summary>
    /// A factory is whatever binds to a delegate-typed parameter — a lambda, a method group, or a
    /// Func variable alike (v0.14.0 QA finding 5) — never decided by the argument's syntax alone.
    /// </summary>
    private static bool IsFactoryArgument(IMethodSymbol method, SeparatedSyntaxList<ArgumentSyntax> arguments, ArgumentSyntax argument) =>
        ParameterFor(method, argument, arguments.IndexOf(argument)) is { Type.TypeKind: TypeKind.Delegate }
        || argument.Expression is LambdaExpressionSyntax or AnonymousMethodExpressionSyntax;

    private static bool IsServiceCollectionReceiver(IMethodSymbol method) =>
        (method.ReducedFrom ?? method).Parameters is [{ Type.Name: "IServiceCollection" }, ..];

    /// <summary>The service key of a keyed registration, as written (a literal or expression).</summary>
    private static string? KeyOf(IMethodSymbol method, SeparatedSyntaxList<ArgumentSyntax> arguments)
    {
        if (!method.Name.Contains("Keyed", StringComparison.Ordinal))
        {
            return null;
        }

        for (int i = 0; i < arguments.Count; i++)
        {
            if (ParameterFor(method, arguments[i], i) is { Name: "serviceKey" })
            {
                return Abbreviate(arguments[i].Expression.ToString());
            }
        }

        return null;
    }

    private static bool IsDiExtension(IMethodSymbol method)
    {
        var original = method.ReducedFrom ?? method;
        return original.IsExtensionMethod
            && !SymbolFacts.IsInSource(original)
            && original.ContainingType?.ContainingNamespace?.ToDisplayString() is { } ns
            && (ns == DiNamespace || ns.StartsWith(DiNamespace + ".", StringComparison.Ordinal));
    }

    /// <summary>Add/TryAdd + optional Keyed + Singleton/Scoped/Transient → the lifetime; anything else → null.</summary>
    private static string? LifetimeOf(string name)
    {
        string rest = name.StartsWith("TryAdd", StringComparison.Ordinal) ? name["TryAdd".Length..]
            : name.StartsWith("Add", StringComparison.Ordinal) ? name["Add".Length..]
            : string.Empty;
        if (rest.StartsWith("Keyed", StringComparison.Ordinal))
        {
            rest = rest["Keyed".Length..];
        }

        return rest is "Singleton" or "Scoped" or "Transient" ? rest : null;
    }

    /// <summary>
    /// The arguments other than a keyed registration's service key and — in static-call form
    /// (<c>ServiceCollectionServiceExtensions.AddScoped&lt;T&gt;(services)</c>) — the collection
    /// itself (v0.14.0 QA finding 5).
    /// </summary>
    private static IEnumerable<ArgumentSyntax> NonKeyArguments(IMethodSymbol method, SeparatedSyntaxList<ArgumentSyntax> arguments)
    {
        bool keyed = method.Name.Contains("Keyed", StringComparison.Ordinal);
        for (int i = 0; i < arguments.Count; i++)
        {
            var parameter = ParameterFor(method, arguments[i], i);
            if (keyed && parameter is { Name: "serviceKey" })
            {
                continue;
            }

            if (parameter is { Ordinal: 0, Type.Name: "IServiceCollection" } && method.ReducedFrom is null && method.IsExtensionMethod)
            {
                continue;
            }

            yield return arguments[i];
        }
    }

    private static IParameterSymbol? ParameterFor(IMethodSymbol method, ArgumentSyntax argument, int index)
    {
        if (argument.NameColon is { } nameColon)
        {
            return method.Parameters.FirstOrDefault(p => p.Name == nameColon.Name.Identifier.ValueText);
        }

        return index >= 0 && index < method.Parameters.Length ? method.Parameters[index] : null;
    }

    private static ITypeSymbol? TypeOfArgument(ExpressionSyntax expression, SemanticModel model, CancellationToken cancellationToken) =>
        expression is TypeOfExpressionSyntax typeOf ? model.GetTypeInfo(typeOf.Type, cancellationToken).Type : null;

    /// <summary>The type a factory lambda constructs, when its body is (or returns) a single <c>new X(...)</c>.</summary>
    private static string? FactoryCreatedType(ExpressionSyntax lambda, SemanticModel model, CancellationToken cancellationToken)
    {
        ExpressionSyntax? created = lambda switch
        {
            LambdaExpressionSyntax { ExpressionBody: { } body } => body,
            LambdaExpressionSyntax { Block.Statements: [ReturnStatementSyntax { Expression: { } returned }] } => returned,
            _ => null,
        };

        return created is BaseObjectCreationExpressionSyntax creation
            && model.GetTypeInfo(creation, cancellationToken).Type is { TypeKind: not TypeKind.Error } type
            ? Fqn(type)
            : null;
    }

    private static string? StaticTypeOf(ExpressionSyntax expression, SemanticModel model, CancellationToken cancellationToken) =>
        model.GetTypeInfo(expression, cancellationToken).Type is { TypeKind: not TypeKind.Error } type ? Fqn(type) : null;

    private static bool IsOpenGeneric(ITypeSymbol type) => type is INamedTypeSymbol { IsUnboundGenericType: true };

    private static string Fqn(ITypeSymbol type) =>
        (type is INamedTypeSymbol { IsUnboundGenericType: true } unbound ? unbound.OriginalDefinition : type)
        .ToDisplayString(SymbolFacts.FqnFormat);

    private static string Abbreviate(string text)
    {
        string singleLine = string.Join(' ', text.Split((char[])['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(static s => s.Trim()));
        return singleLine.Length <= 160 ? singleLine : singleLine[..157] + "...";
    }
}
