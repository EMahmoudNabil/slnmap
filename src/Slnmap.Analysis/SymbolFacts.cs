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

    /// <summary>
    /// The Program class holding a project's top-level statements — synthesized, or explicitly
    /// declared (`public partial class Program { }`, the WebApplicationFactory pattern; partial
    /// declarations are one symbol). An ordinary Program class inside a namespace does not match.
    /// </summary>
    private static bool IsTopLevelProgramType(ISymbol definition) =>
        definition is INamedTypeSymbol { Name: WellKnownMemberNames.TopLevelStatementsEntryPointTypeName, TypeKind: TypeKind.Class } type
        && type.ContainingNamespace is { IsGlobalNamespace: true }
        && !type.GetMembers(WellKnownMemberNames.TopLevelStatementsEntryPointMethodName).IsEmpty;

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
        // Enum members are IFieldSymbol in Roslyn's model too, but they're enumerants, not state:
        // they get their own kind (#13) so Field censuses stay honest. The declaration walk
        // guarantees ALL members materialize, referenced or not (the v0.6.1 census-consistency
        // objection); the reference filter then picks their usage edges up for free.
        IFieldSymbol { ContainingType.TypeKind: TypeKind.Enum } => NodeKind.EnumMember,
        IFieldSymbol => NodeKind.Field,
        IEventSymbol => NodeKind.Event,
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

        // Anonymous types (and their properties) and named tuple elements produce no nodes.
        // They are unnameable and unqueryable, and structurally identical ones in DIFFERENT
        // files render the same FQN ("<anonymous type: int Id, string Label>",
        // "(string From, string To).From") — FQN is node identity, so they'd collapse into one
        // node pinned to whichever file was analyzed first, fabricating cross-project References
        // edges between projects that share no real code (v0.6.1). Tuple elements additionally
        // float uncontained: their containing tuple type is never a node (its OriginalDefinition
        // is the metadata ValueTuple), so nothing would even connect them to the graph.
        if (definition is INamedTypeSymbol { IsAnonymousType: true }
            || definition.ContainingType is { IsAnonymousType: true } or { IsTupleType: true })
        {
            return null;
        }

        string fqn = definition.ToDisplayString(FqnFormat);

        // The top-level-statements construct renders namespace-less FQNs — the entry point as a
        // bare "<top-level-statements-entry-point>" and its containing Program class as a bare
        // "Program" — so two projects' entry points (and Program classes, synthesized or explicit
        // `partial class Program`) would collide on FQN. FQNs are node identity: merged nodes make
        // incremental eviction attribute one project's edges to the other's file and silently drop
        // them. Qualify both with the assembly (project) name: deterministic across machines,
        // unlike a file path.
        if ((fqn == "<top-level-statements-entry-point>" || IsTopLevelProgramType(definition))
            && definition.ContainingAssembly is { } assembly)
        {
            fqn = $"{assembly.Name}.{fqn}";
        }

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
