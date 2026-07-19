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
public sealed record SymbolNode(
    string Id,
    NodeKind Kind,
    string Name,
    string Fqn,
    string? FilePath = null,
    SourceSpan? Span = null)
{
    /// <summary>Creates a node with its <see cref="Id"/> derived from <paramref name="kind"/> and <paramref name="fqn"/>.</summary>
    public static SymbolNode Create(NodeKind kind, string name, string fqn, string? filePath = null, SourceSpan? span = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(fqn);
        return new SymbolNode(CreateId(kind, fqn), kind, name, fqn, filePath, span);
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
