using Microsoft.CodeAnalysis;

namespace Slnmap.Analysis;

/// <summary>
/// Detects ASP.NET Core Razor Pages (PageModel-derived classes with handler methods) purely to
/// disclose them as a known, unmodeled routing system — mirrors
/// <see cref="ControllerEndpointFacts"/>'s conventional-routing disclosure (v0.12.2,
/// foreign-patterns-trial finding #2: a real Razor Pages solution had NO disclosure of any kind —
/// no counter, no warning, no Endpoint node — despite the CHANGELOG already, and wrongly, claiming
/// "detected and disclosed where applicable" for Razor Pages specifically). No route extraction:
/// Razor Pages route by file location under the project's <c>Pages/</c> folder, a build-time
/// convention this tool cannot resolve from syntax/semantics alone — disclosure only, the same
/// scope conventionally-routed controllers already get.
/// </summary>
internal static class RazorPageFacts
{
    private const string RazorPagesNamespace = "Microsoft.AspNetCore.Mvc.RazorPages";

    /// <summary>The <c>On&lt;Verb&gt;[&lt;HandlerName&gt;][Async]</c> naming convention's verb prefixes.</summary>
    private static readonly string[] HandlerVerbPrefixes =
        ["OnGet", "OnPost", "OnPut", "OnDelete", "OnHead", "OnOptions", "OnPatch"];

    /// <summary>
    /// True when <paramref name="method"/> is a Razor Pages handler method on a class that
    /// transitively derives from <c>Microsoft.AspNetCore.Mvc.RazorPages.PageModel</c>. The
    /// base-chain walk runs first (pure symbol-pointer chasing) before the name check, mirroring
    /// <see cref="ControllerEndpointFacts.IsController"/>'s ordering rationale.
    /// </summary>
    public static bool IsPageHandler(IMethodSymbol method)
    {
        if (method.MethodKind != MethodKind.Ordinary
            || method.DeclaredAccessibility != Accessibility.Public
            || method.IsStatic)
        {
            return false;
        }

        if (method.ContainingType is not { } type || !IsPageModel(type))
        {
            return false;
        }

        foreach (string prefix in HandlerVerbPrefixes)
        {
            if (method.Name.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsPageModel(INamedTypeSymbol type)
    {
        for (var baseType = type.BaseType; baseType is not null; baseType = baseType.BaseType)
        {
            if (baseType.Name == "PageModel" && baseType.ContainingNamespace?.ToDisplayString() == RazorPagesNamespace)
            {
                return true;
            }
        }

        return false;
    }
}
