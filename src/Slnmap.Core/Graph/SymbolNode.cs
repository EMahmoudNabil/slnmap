using System.Security.Cryptography;
using System.Text;

namespace Slnmap.Core.Graph;

/// <summary>
/// A node in the code graph: one symbol (project, namespace, type, or member).
/// </summary>
/// <param name="Id">Stable identifier derived from <paramref name="Kind"/> and <paramref name="Fqn"/>; see <see cref="CreateId"/>.</param>
/// <param name="Kind">What kind of symbol this is.</param>
/// <param name="Name">Short name, e.g. <c>AnalyzeAsync</c>.</param>
/// <param name="Fqn">Fully qualified name, e.g. <c>Slnmap.Core.Analysis.ISolutionAnalyzer.AnalyzeAsync(string)</c>.</param>
/// <param name="FilePath">Path of the file declaring the symbol, or null when it has no single location (e.g. a project or partial type).</param>
/// <param name="Span">Character span of the declaration within <paramref name="FilePath"/>.</param>
/// <param name="Accessibility">The symbol's declared accessibility as Roslyn names it (<c>Public</c>, <c>Internal</c>, <c>Private</c>, <c>Protected</c>, <c>ProtectedOrInternal</c>, <c>ProtectedAndInternal</c>), or null where it does not apply (projects, namespaces, endpoints, frontend nodes). Schema v2.</param>
/// <param name="MemberFlags">Comma-separated facts about a member that the edges cannot show (schema v2): <c>override</c> (overrides a base member) and/or <c>interface-impl</c> (implements an interface member, explicitly or implicitly — including external interfaces such as <c>IDisposable</c>). Such a member is reached through its base/interface, so it has no direct callers of its own. On a type: <c>external-base</c> (derives from or implements a type outside the solution — the shape of framework-discovered types). Null when none applies.</param>
public sealed record SymbolNode(
    string Id,
    NodeKind Kind,
    string Name,
    string Fqn,
    string? FilePath = null,
    SourceSpan? Span = null,
    string? Accessibility = null,
    string? MemberFlags = null)
{
    /// <summary>Creates a node with its <see cref="Id"/> derived from <paramref name="kind"/> and <paramref name="fqn"/>.</summary>
    public static SymbolNode Create(
        NodeKind kind,
        string name,
        string fqn,
        string? filePath = null,
        SourceSpan? span = null,
        string? accessibility = null,
        string? memberFlags = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(fqn);
        return new SymbolNode(CreateId(kind, fqn), kind, name, fqn, filePath, span, accessibility, memberFlags);
    }

    /// <summary>
    /// Derives the stable node id: lowercase hex of the first 16 bytes of SHA-256 over <c>{kind}:{fqn}</c>.
    /// Deterministic across runs and machines, so re-analysis produces identical ids for unchanged symbols.
    /// </summary>
    public static string CreateId(NodeKind kind, string fqn)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fqn);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{(int)kind}:{fqn}"));
        return Convert.ToHexStringLower(hash.AsSpan(0, 16));
    }
}
