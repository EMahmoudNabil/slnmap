using System.Text.Json;
using System.Text.Json.Serialization;
using Slnmap.Core.Graph;

namespace Slnmap.Analysis;

/// <summary>
/// Raised for a malformed or unsupported `slnmap-ts` JSON artifact — caught at the CLI boundary
/// and reported without a stack trace, in the spirit of slnmap's own CLI error conventions
/// (corrective, actionable; see <c>Slnmap.Analysis.TsConfigError</c>'s counterpart on the Node
/// side, which this deliberately mirrors).
/// </summary>
public sealed class TsArtifactException(string message) : Exception(message);

/// <summary>One call site as the `slnmap-ts` JSON artifact represents it (schemaVersion 2).</summary>
public sealed record TsArtifactCallSite(
    [property: JsonPropertyName("kind")] string? Kind,
    [property: JsonPropertyName("verb")] string? Verb,
    [property: JsonPropertyName("template")] string? Template,
    [property: JsonPropertyName("resolutionTier")] string? ResolutionTier,
    [property: JsonPropertyName("category")] string? Category,
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("file")] string? File,
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("column")] int Column,
    [property: JsonPropertyName("spanStart")] int SpanStart,
    [property: JsonPropertyName("spanEnd")] int SpanEnd);

public sealed record TsArtifactStats(
    [property: JsonPropertyName("resolvedCount")] int ResolvedCount,
    [property: JsonPropertyName("unresolvedCount")] int UnresolvedCount,
    [property: JsonPropertyName("coveragePercent")] double CoveragePercent,
    [property: JsonPropertyName("byCategory")] Dictionary<string, int>? ByCategory);

public sealed record TsArtifact(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("producer")] string? Producer,
    [property: JsonPropertyName("producerVersion")] string? ProducerVersion,
    [property: JsonPropertyName("stats")] TsArtifactStats? Stats,
    [property: JsonPropertyName("callSites")] IReadOnlyList<TsArtifactCallSite>? CallSites);

/// <summary>
/// Parsing, validation, and graph-node construction for the `slnmap-ts` JSON artifact
/// (reports/ts-extractor-investigation.md §Q1/§Q2; schemaVersion 2 per
/// reports/analyze-ts-verb-report.md Part 0). Everything here is pure — no SQLite, no process
/// spawning; the `analyze-ts` CLI verb (Program.cs) orchestrates around it.
/// </summary>
public static class TsArtifactFacts
{
    public const int SupportedSchemaVersion = 2;
    private const string ExpectedProducer = "slnmap-ts";

    /// <summary>
    /// Parses and fully validates the artifact — every call site is checked before any node is
    /// built from any of them, so a malformed artifact is always a clean failure, never a
    /// partial ingest (Task B Part 2 step 4).
    /// </summary>
    public static TsArtifact Parse(string json)
    {
        TsArtifact? artifact;
        try
        {
            artifact = JsonSerializer.Deserialize<TsArtifact>(json);
        }
        catch (JsonException e)
        {
            throw new TsArtifactException($"Malformed slnmap-ts artifact: not valid JSON ({e.Message}).");
        }

        if (artifact is null)
        {
            throw new TsArtifactException("Malformed slnmap-ts artifact: the JSON document is empty or 'null'.");
        }

        Validate(artifact);
        return artifact;
    }

    private static void Validate(TsArtifact artifact)
    {
        if (artifact.SchemaVersion == 1)
        {
            throw new TsArtifactException(
                "This artifact was produced by slnmap-ts < 0.2.0 (schemaVersion 1), which analyze-ts no " +
                "longer supports — it predates spanStart/spanEnd. Re-run extraction with slnmap-ts >= 0.2.0.");
        }

        if (artifact.SchemaVersion != SupportedSchemaVersion)
        {
            throw new TsArtifactException(
                $"Unsupported slnmap-ts artifact schemaVersion {artifact.SchemaVersion} (expected {SupportedSchemaVersion}).");
        }

        if (!string.Equals(artifact.Producer, ExpectedProducer, StringComparison.Ordinal))
        {
            throw new TsArtifactException(
                $"Unexpected artifact producer '{artifact.Producer ?? "(none)"}' (expected '{ExpectedProducer}').");
        }

        if (artifact.CallSites is null)
        {
            throw new TsArtifactException("Malformed slnmap-ts artifact: 'callSites' is missing.");
        }

        for (int i = 0; i < artifact.CallSites.Count; i++)
        {
            ValidateCallSite(artifact.CallSites[i], i);
        }
    }

    private static void ValidateCallSite(TsArtifactCallSite site, int index)
    {
        if (string.IsNullOrEmpty(site.File))
        {
            throw new TsArtifactException($"Malformed slnmap-ts artifact: callSites[{index}] is missing 'file'.");
        }

        if (string.IsNullOrEmpty(site.Verb))
        {
            throw new TsArtifactException($"Malformed slnmap-ts artifact: callSites[{index}] is missing 'verb'.");
        }

        switch (site.Kind)
        {
            case "FrontendCallSite":
                if (string.IsNullOrEmpty(site.Template))
                {
                    throw new TsArtifactException(
                        $"Malformed slnmap-ts artifact: callSites[{index}] (FrontendCallSite) is missing 'template'.");
                }

                break;
            case "UnresolvedCallSite":
                if (string.IsNullOrEmpty(site.Category) || string.IsNullOrEmpty(site.Reason))
                {
                    throw new TsArtifactException(
                        $"Malformed slnmap-ts artifact: callSites[{index}] (UnresolvedCallSite) is missing 'category' or 'reason'.");
                }

                break;
            default:
                throw new TsArtifactException(
                    $"Malformed slnmap-ts artifact: callSites[{index}] has unrecognized kind '{site.Kind ?? "(none)"}'.");
        }
    }

    /// <summary>
    /// Builds the frontend graph nodes from a validated artifact. Identity (<c>fqn</c>) is built
    /// from the artifact's project-root-relative <c>file</c> — portable across machines, per the
    /// investigation §Q2.2 scheme (<c>"{VERB} {relativeFile}:{line}:{column}"</c> /
    /// <c>"{VERB-or-UNKNOWN} {category} {relativeFile}:{line}:{column}"</c>). The <c>file</c>/span
    /// columns instead store the ABSOLUTE path (resolved against <paramref name="frontendRoot"/>)
    /// — matching the Roslyn side's own convention of storing whatever MSBuildWorkspace's syntax
    /// tree gives it (an absolute path), so file-based navigation and incremental tooling work
    /// identically for nodes from either producer. Node identity therefore stays portable
    /// (survives the frontend root moving to a different path on a different machine) while
    /// navigation data is machine-accurate — the same split the Roslyn side already has between
    /// its symbol-based <c>fqn</c> and its absolute <c>file</c> column.
    /// </summary>
    public static IReadOnlyList<SymbolNode> BuildNodes(TsArtifact artifact, string frontendRoot)
    {
        var nodes = new List<SymbolNode>(artifact.CallSites!.Count);
        foreach (var site in artifact.CallSites)
        {
            string relativeFile = site.File!;
            string absoluteFile = Path.GetFullPath(Path.Combine(frontendRoot, relativeFile.Replace('/', Path.DirectorySeparatorChar)));
            var span = new SourceSpan(site.SpanStart, site.SpanEnd);
            string location = $"{relativeFile}:{site.Line}:{site.Column}";

            SymbolNode node = site.Kind == "FrontendCallSite"
                ? SymbolNode.Create(
                    NodeKind.FrontendCallSite,
                    name: site.Template!,
                    fqn: $"{site.Verb} {location}",
                    filePath: absoluteFile,
                    span: span)
                : SymbolNode.Create(
                    NodeKind.UnresolvedCallSite,
                    name: $"{site.Category}: {site.Reason}",
                    fqn: $"{site.Verb} {site.Category} {location}",
                    filePath: absoluteFile,
                    span: span);

            nodes.Add(node);
        }

        return nodes;
    }

    /// <summary>
    /// Kind-scoped prune-and-replace (investigation §Q1.2): a NEW graph containing
    /// <paramref name="existing"/>'s nodes and edges MINUS any existing
    /// <see cref="NodeKind.FrontendCallSite"/>/<see cref="NodeKind.UnresolvedCallSite"/> nodes,
    /// plus <paramref name="newNodes"/>. Expressed as "build a graph without the stale kinds"
    /// rather than adding a removal API to <see cref="CodeGraph"/> — nothing else in slnmap
    /// needs node removal.
    ///
    /// cross-stack-linker-investigation.md §Q3 prerequisite: this used to carry every existing
    /// edge over unconditionally, correct only while these two kinds carried zero edges. Now
    /// that the cross-stack linker attaches <c>CallsEndpoint</c> edges to
    /// <see cref="NodeKind.FrontendCallSite"/> nodes, an edge sourced from (or targeting) a
    /// pruned node must not survive as a dangling row — nodes are added to <paramref name="merged"/>
    /// FIRST, then edges are kept only when both endpoints are still present, mirroring
    /// <c>SolutionAnalysisEngine.PruneDanglingEdges</c>'s already-shipped, kind-agnostic
    /// existence check for the exact same reason.
    /// </summary>
    public static CodeGraph MergeIntoGraph(CodeGraph existing, IReadOnlyList<SymbolNode> newNodes)
    {
        var merged = new CodeGraph();
        foreach (var node in existing.Nodes)
        {
            if (node.Kind is NodeKind.FrontendCallSite or NodeKind.UnresolvedCallSite)
            {
                continue;
            }

            merged.AddNode(node);
        }

        foreach (var node in newNodes)
        {
            merged.AddNode(node);
        }

        foreach (var edge in existing.Edges)
        {
            if (merged.ContainsNode(edge.SourceId) && merged.ContainsNode(edge.TargetId))
            {
                merged.AddEdge(edge);
            }
        }

        return merged;
    }
}
