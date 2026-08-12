using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;

namespace Slnmap.Analysis;

/// <summary>
/// The outcome of classifying one controller method: zero or more resolved routes, zero or more
/// counted refusals, and whether the method marks its class as conventionally routed (a different
/// routing system — noted, not counted). A method that is not an action at all (not public, not
/// ordinary, [NonAction], not on a controller) classifies to null.
/// </summary>
internal sealed record ControllerActionClassification(
    IReadOnlyList<(string Verb, string Template)> Routes,
    IReadOnlyList<string> UnresolvedReasons,
    bool IsConventionallyRouted);

/// <summary>
/// Pure semantic-model extraction for attribute-routed ASP.NET Core controllers (the v1.1
/// controller-endpoints investigation). Everything reads <see cref="ISymbol.GetAttributes"/> —
/// attribute constructor arguments are compile-time constants by definition, so unlike the
/// Minimal-API extractor there is no constant folding, receiver tracing, or forwarder machinery.
/// Selector semantics mirror MVC's own model: attributes carrying a route template each produce a
/// route; template-less verb attributes constrain those routes (or, with no templated attribute,
/// use the class-level template alone). Deterministic-or-declared throughout: anything outside
/// the supported shapes is counted with a reason, never guessed.
/// </summary>
internal static partial class ControllerEndpointFacts
{
    private const string MvcNamespace = "Microsoft.AspNetCore.Mvc";

    private static readonly Dictionary<string, string> VerbAttributes = new(StringComparer.Ordinal)
    {
        ["HttpGetAttribute"] = "GET",
        ["HttpPostAttribute"] = "POST",
        ["HttpPutAttribute"] = "PUT",
        ["HttpDeleteAttribute"] = "DELETE",
        ["HttpPatchAttribute"] = "PATCH",
    };

    private static readonly string[] UnsupportedVerbAttributes =
        ["HttpHeadAttribute", "HttpOptionsAttribute", "AcceptVerbsAttribute"];

    [GeneratedRegex(@"\[(\w+)\]")]
    private static partial Regex RouteToken();

    /// <summary>
    /// Classifies a method declaration as a controller action (or not). Returns null when the
    /// method cannot be an action (wrong shape, [NonAction], not on a controller type) — callers
    /// emit nothing and count nothing for those.
    /// </summary>
    public static ControllerActionClassification? Classify(IMethodSymbol method)
    {
        if (method.MethodKind != MethodKind.Ordinary
            || method.DeclaredAccessibility != Accessibility.Public
            || method.IsStatic)
        {
            return null;
        }

        var type = method.ContainingType;
        if (type is null || !IsController(type))
        {
            return null;
        }

        if (HasAttribute(method, "NonActionAttribute"))
        {
            return null;
        }

        // Gather the action's routing attributes.
        var templatedProviders = new List<(string? Verb, string Template)>();
        var bareVerbs = new List<string>();
        var refusals = new List<string>();
        foreach (var attribute in method.GetAttributes())
        {
            if (attribute.AttributeClass is not { } attributeClass || !IsMvcAttribute(attributeClass))
            {
                continue;
            }

            string name = attributeClass.Name;
            if (VerbAttributes.TryGetValue(name, out string? verb))
            {
                if (FirstStringArgument(attribute) is { } template)
                {
                    templatedProviders.Add((verb, template));
                }
                else
                {
                    bareVerbs.Add(verb);
                }
            }
            else if (name == "RouteAttribute")
            {
                if (FirstStringArgument(attribute) is { } template)
                {
                    templatedProviders.Add((null, template));
                }
            }
            else if (UnsupportedVerbAttributes.Contains(name, StringComparer.Ordinal))
            {
                refusals.Add($"'{name[..^"Attribute".Length]}' is outside the modeled verb set (GET/POST/PUT/DELETE/PATCH)");
            }
        }

        var classTemplates = CollectClassRouteTemplates(type);

        // No template anywhere = conventional routing (bare verb attributes are just verb
        // constraints on conventional routes — the eShop Identity shape). A different routing
        // system working as designed: noted once per class, never counted as a failure.
        bool hasAnyTemplate = classTemplates.Count > 0 || templatedProviders.Count > 0;
        if (!hasAnyTemplate)
        {
            return refusals.Count > 0
                ? new ControllerActionClassification([], refusals, IsConventionallyRouted: false)
                : new ControllerActionClassification([], [], IsConventionallyRouted: true);
        }

        // Attribute-routed action on an abstract controller: the [controller] token (and the
        // route table entry itself) binds to each derived type at runtime — one route per
        // derivative. Counted, not enumerated (v1.2 candidate; zero live field samples).
        if (type.IsAbstract)
        {
            refusals.Add($"declared on abstract controller '{type.Name}' — its route binds to each derived type; not enumerated");
            return new ControllerActionClassification([], refusals, IsConventionallyRouted: false);
        }

        // MVC selector semantics: every templated provider yields a route; its verb is its own
        // (verb attributes) unioned with the template-less verb attributes. A templated [Route]
        // takes the bare-verb constraints; with no verb anywhere the action matches every HTTP
        // verb — refused, because picking one would be a guess.
        var routes = new List<(string Verb, string Template)>();
        if (templatedProviders.Count > 0)
        {
            foreach (var (providerVerb, template) in templatedProviders)
            {
                var verbs = providerVerb is null ? bareVerbs : [providerVerb, .. bareVerbs];
                if (verbs.Count == 0)
                {
                    refusals.Add($"route '{template}' has no HTTP-verb attribute — it matches every verb");
                    continue;
                }

                foreach (string verb in verbs.Distinct(StringComparer.Ordinal))
                {
                    AddComposedRoutes(routes, refusals, classTemplates, template, verb, method, type);
                }
            }
        }
        else
        {
            // Bare verb attributes only: the class template alone carries the route. A public
            // method with NO routing attributes at all on a class-routed controller is still an
            // action — matching every verb, refused for the same reason as above.
            if (bareVerbs.Count == 0 && refusals.Count == 0)
            {
                refusals.Add("action has no HTTP-verb attribute — it matches every verb");
            }

            foreach (string verb in bareVerbs.Distinct(StringComparer.Ordinal))
            {
                AddComposedRoutes(routes, refusals, classTemplates, actionTemplate: null, verb, method, type);
            }
        }

        return new ControllerActionClassification(routes, refusals, IsConventionallyRouted: false);
    }

    /// <summary>
    /// True when the type transitively derives from <c>Microsoft.AspNetCore.Mvc.ControllerBase</c>
    /// (covers <c>Controller</c>), and is not opted out via [NonController]. The base-chain walk
    /// runs FIRST: it is pure symbol-pointer chasing, while GetAttributes() binds attribute data —
    /// on a large solution this check runs for every method in every class with a base list, and
    /// almost none of them are controllers.
    /// </summary>
    public static bool IsController(INamedTypeSymbol type)
    {
        for (var baseType = type.BaseType; baseType is not null; baseType = baseType.BaseType)
        {
            if (baseType.Name == "ControllerBase" && IsMvcType(baseType))
            {
                return !HasAttribute(type, "NonControllerAttribute");
            }
        }

        return false;
    }

    /// <summary>
    /// The class-level [Route] templates: the type's own if declared, else the nearest base
    /// class's (RouteAttribute is Inherited = true; ASP.NET honors the most-derived declarations).
    /// </summary>
    private static IReadOnlyList<string> CollectClassRouteTemplates(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? current = type; current is not null; current = current.BaseType)
        {
            var templates = current.GetAttributes()
                .Where(a => a.AttributeClass is { Name: "RouteAttribute" } attributeClass && IsMvcAttribute(attributeClass))
                .Select(FirstStringArgument)
                .OfType<string>()
                .ToList();
            if (templates.Count > 0)
            {
                return templates;
            }
        }

        return [];
    }

    /// <summary>
    /// Composes class × action templates for one verb, substitutes tokens, and appends the result
    /// — or a refusal when a token cannot be resolved. An action template starting with '/' or
    /// '~/' is absolute and ignores the class prefix (ASP.NET's own rule).
    /// </summary>
    private static void AddComposedRoutes(
        List<(string Verb, string Template)> routes,
        List<string> refusals,
        IReadOnlyList<string> classTemplates,
        string? actionTemplate,
        string verb,
        IMethodSymbol method,
        INamedTypeSymbol type)
    {
        var combined = new List<string>();
        if (actionTemplate is not null && IsAbsolute(actionTemplate, out string absolute))
        {
            combined.Add(absolute);
        }
        else if (classTemplates.Count == 0)
        {
            if (actionTemplate is not null)
            {
                combined.Add(actionTemplate);
            }
        }
        else
        {
            foreach (string classTemplate in classTemplates)
            {
                combined.Add(actionTemplate is null ? classTemplate : classTemplate + "/" + actionTemplate);
            }
        }

        foreach (string template in combined)
        {
            if (TrySubstituteTokens(template, method, type, out string substituted, out string? reason))
            {
                routes.Add((verb, EndpointFacts.ComposeTemplate(string.Empty, substituted)));
            }
            else
            {
                refusals.Add(reason!);
            }
        }
    }

    private static bool IsAbsolute(string template, out string normalized)
    {
        if (template.StartsWith("~/", StringComparison.Ordinal))
        {
            normalized = template[1..];
            return true;
        }

        if (template.StartsWith('/'))
        {
            normalized = template;
            return true;
        }

        normalized = template;
        return false;
    }

    /// <summary>
    /// Substitutes the MVC route tokens, case-insensitively: [controller] = class name minus a
    /// trailing "Controller", [action] = method name minus a trailing "Async" (MVC's action-name
    /// convention), [area] = the [Area("…")] value. Any other token — or [area] without an [Area]
    /// attribute — refuses.
    /// </summary>
    private static bool TrySubstituteTokens(
        string template,
        IMethodSymbol method,
        INamedTypeSymbol type,
        out string substituted,
        out string? reason)
    {
        string? failure = null;
        substituted = RouteToken().Replace(template, match =>
        {
            string token = match.Groups[1].Value;
            if (token.Equals("controller", StringComparison.OrdinalIgnoreCase))
            {
                return TrimSuffix(type.Name, "Controller");
            }

            if (token.Equals("action", StringComparison.OrdinalIgnoreCase))
            {
                return TrimSuffix(method.Name, "Async");
            }

            if (token.Equals("area", StringComparison.OrdinalIgnoreCase))
            {
                if (FindAreaName(type) is { } area)
                {
                    return area;
                }

                failure = $"route '{template}' uses [area] but the controller has no [Area] attribute";
                return match.Value;
            }

            failure = $"route '{template}' uses unrecognized token [{token}]";
            return match.Value;
        });

        reason = failure;
        return failure is null;
    }

    private static string? FindAreaName(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? current = type; current is not null; current = current.BaseType)
        {
            var area = current.GetAttributes()
                .FirstOrDefault(a => a.AttributeClass is { Name: "AreaAttribute" } attributeClass && IsMvcAttribute(attributeClass));
            if (area is not null)
            {
                return FirstStringArgument(area);
            }
        }

        return null;
    }

    private static string TrimSuffix(string name, string suffix) =>
        name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.Ordinal)
            ? name[..^suffix.Length]
            : name;

    private static string? FirstStringArgument(AttributeData attribute) =>
        attribute.ConstructorArguments.Length > 0 && attribute.ConstructorArguments[0].Value is string s
            ? s
            : null;

    private static bool HasAttribute(ISymbol symbol, string attributeName) =>
        symbol.GetAttributes().Any(a => a.AttributeClass is { } attributeClass
            && attributeClass.Name == attributeName
            && IsMvcAttribute(attributeClass));

    private static bool IsMvcAttribute(INamedTypeSymbol attributeClass) =>
        attributeClass.ContainingNamespace?.ToDisplayString() == MvcNamespace;

    private static bool IsMvcType(INamedTypeSymbol type) =>
        type.ContainingNamespace?.ToDisplayString() == MvcNamespace;
}
