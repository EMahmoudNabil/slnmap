using Slnmap.Core.Graph;

namespace Slnmap.Core.Storage;

/// <summary>Persists and queries a code graph.</summary>
public interface IGraphStore : IAsyncDisposable
{
    /// <summary>
    /// Ensures the database file and schema exist (creating an empty graph if absent).
    /// Must be called before any read member. The write path (<see cref="SaveAsync"/>)
    /// initializes its own database and does not require this first.
    /// </summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically replaces the stored graph with <paramref name="graph"/>, the file hashes in
    /// <paramref name="files"/>, and the metadata in <paramref name="meta"/>. The new graph is
    /// built in a temporary database and swapped in only on success, so an interrupted call never
    /// corrupts or truncates the existing graph.
    /// </summary>
    Task SaveAsync(
        CodeGraph graph,
        IEnumerable<FileRecord> files,
        IReadOnlyDictionary<string, string> meta,
        CancellationToken cancellationToken = default);

    /// <summary>Loads the full stored graph into memory.</summary>
    Task<CodeGraph> LoadGraphAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// All nodes whose fully qualified name equals <paramref name="fqn"/> exactly (case-sensitive,
    /// no wildcards). Usually one, but a single FQN can map to several nodes of different kinds, so
    /// callers must not assume uniqueness. Empty when nothing matches.
    /// </summary>
    Task<IReadOnlyList<SymbolNode>> GetNodesByFqnAsync(string fqn, CancellationToken cancellationToken = default);

    /// <summary>Fetches the nodes with the given ids (order unspecified, missing ids omitted).</summary>
    Task<IReadOnlyList<SymbolNode>> GetNodesByIdsAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default);

    /// <summary>All nodes of the given <paramref name="kind"/>.</summary>
    Task<IReadOnlyList<SymbolNode>> GetNodesByKindAsync(NodeKind kind, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds up to <paramref name="limit"/> nodes whose name or fully qualified name matches
    /// <paramref name="pattern"/> (SQL LIKE semantics, <c>%</c> and <c>_</c> wildcards).
    /// </summary>
    Task<IReadOnlyList<SymbolNode>> FindNodesAsync(
        string pattern,
        int limit = 50,
        CancellationToken cancellationToken = default);

    /// <summary>Returns edges touching <paramref name="nodeId"/> in the given direction, optionally filtered by kind.</summary>
    Task<IReadOnlyList<RelationshipEdge>> GetEdgesAsync(
        string nodeId,
        EdgeDirection direction,
        RelationshipKind? kind = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Transitively reaches nodes connected to <paramref name="startId"/> along dependency edges
    /// (every kind except structural <see cref="RelationshipKind.Contains"/>), via a recursive query
    /// over the edge table. <see cref="EdgeDirection.Incoming"/> yields dependents ("what breaks if
    /// this changes"); <see cref="EdgeDirection.Outgoing"/> yields dependencies. Results are tagged
    /// with shortest depth, bounded by <paramref name="maxDepth"/> hops and <paramref name="maxResults"/> rows.
    /// </summary>
    Task<IReadOnlyList<ReachableNode>> TraverseAsync(
        string startId,
        EdgeDirection direction,
        int maxDepth = 5,
        int maxResults = 500,
        CancellationToken cancellationToken = default);

    /// <summary>Counts-first summary of the stored graph.</summary>
    Task<GraphStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default);

    /// <summary>All metadata key/value pairs (see <see cref="MetaKeys"/>).</summary>
    Task<IReadOnlyDictionary<string, string>> GetMetaAsync(CancellationToken cancellationToken = default);

    /// <summary>Content hashes of all files seen by the last analysis, keyed by path. Used for incremental re-analysis.</summary>
    Task<IReadOnlyDictionary<string, string>> GetFileHashesAsync(CancellationToken cancellationToken = default);
}

/// <summary>Edge traversal direction relative to a node.</summary>
public enum EdgeDirection
{
    Outgoing = 0,
    Incoming = 1,
    Both = 2,
}
