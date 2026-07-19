using Microsoft.CodeAnalysis;
using Slnmap.Core.Graph;

namespace Slnmap.Analysis;

/// <summary>Maps Roslyn symbols onto the Slnmap domain model.</summary>
internal static class SymbolFacts
{
    /// <summary>
    /// The display format used for node FQNs: fully qualified, generic type parameters,
    /// method parameter types (to disambiguate overloads), C# keywords for special types.
    /// FQNs are node identity — changing this format invalidates every stored graph.
    /// </summary>
    public static readonly SymbolDisplayFormat FqnFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeContainingType,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    public static bool IsInSource(ISymbol symbol) =>
        symbol.Locations.Any(static location => location.IsInSource);

    public static NodeKind? MapKind(ISymbol symbol) => symbol switch
    {
        INamedTypeSymbol type => type.TypeKind switch
        {
            TypeKind.Class => type.IsRecord ? NodeKind.Record : NodeKind.Class,
            TypeKind.Struct => type.IsRecord ? NodeKind.Record : NodeKind.Struct,
            TypeKind.Interface => NodeKind.Interface,
            TypeKind.Enum => NodeKind.Enum,
            TypeKind.Delegate => NodeKind.Delegate,
            _ => null,
        },
        IMethodSymbol method => method.MethodKind switch
        {
            MethodKind.Constructor or MethodKind.StaticConstructor => NodeKind.Constructor,
            MethodKind.Ordinary or MethodKind.ExplicitInterfaceImplementation
                or MethodKind.UserDefinedOperator or MethodKind.Conversion => NodeKind.Method,
            _ => null,
        },
        IPropertySymbol => NodeKind.Property,
        INamespaceSymbol => NodeKind.Namespace,
        _ => null,
    };

    /// <summary>
    /// Creates the node for a symbol, or null when the symbol is not declared in source
    /// or has no mapped <see cref="NodeKind"/>. Always operates on the original definition,
    /// so constructed generics collapse onto their declaration.
    /// </summary>
    public static SymbolNode? TryCreateNode(ISymbol symbol)
    {
        var definition = symbol.OriginalDefinition;
        if (MapKind(definition) is not { } kind || !IsInSource(definition))
        {
            return null;
        }

        string fqn = definition.ToDisplayString(FqnFormat);
        string name = definition.Name.Length > 0 ? definition.Name : fqn;

        string? file = null;
        SourceSpan? span = null;
        if (kind != NodeKind.Namespace)
        {
            // Namespaces span many files; every other symbol gets its first source declaration.
            var location = definition.Locations.FirstOrDefault(static l => l.IsInSource);
            if (location is { SourceTree: { } tree })
            {
                file = tree.FilePath;
                span = new SourceSpan(location.SourceSpan.Start, location.SourceSpan.End);
            }
        }

        return SymbolNode.Create(kind, name, fqn, file, span);
    }
}
