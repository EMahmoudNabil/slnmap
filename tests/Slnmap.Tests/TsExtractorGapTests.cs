using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Slnmap.Core.Graph;
using Slnmap.Storage;
using Xunit;

namespace Slnmap.Tests;

/// <summary>
/// `slnmap-ts` frontend extractor (Task A: reports/ts-extractor-implementation.md; Task B: the
/// `analyze-ts` verb + ingestion, reports/analyze-ts-verb-report.md). Originally written during
/// the investigation phase, when neither the npm package nor the CLI verb existed —
/// `Gap_AnalyzeTsVerb_ExtractsFixtureCallSitesIntoTheDatabase` was EXPECTED TO FAIL then (the
/// verb was unrecognized). Task B implements the verb and ingestion for real, so this specific
/// test now PASSES — left in place and unrenamed (the same convention EndpointNodeGapTests.cs
/// followed: a test that pins a designed shape stays where it is once that shape ships, rather
/// than being deleted or relocated). `Sanity_*` tests validate schema/migration claims
/// empirically; one of them (kind-name round-trip) is UPDATED here because its claim inverted
/// once NodeKind actually gained the two members (see its own comment). Deeper ingestion tests
/// (kind-scoped prune, fqn/name construction, span columns, idempotence) live in
/// AnalyzeTsIngestionTests.cs, mirroring EndpointNodeTests.cs's relationship to
/// EndpointNodeGapTests.cs. Fixture: tests/fixtures-ts/frontend-fixture/.
/// </summary>
public sealed class TsExtractorGapTests
{
    [Fact]
    public async Task Gap_AnalyzeTsVerb_ExtractsFixtureCallSitesIntoTheDatabase()
    {
        // Designed behavior (reports/ts-extractor-investigation.md §Q1.1): `slnmap analyze-ts
        // <path> --db <db>` exits 0 and populates the database with FrontendCallSite /
        // UnresolvedCallSite nodes for tests/fixtures-ts/frontend-fixture/, matching
        // expected-callsites.json. Implemented in Task B — now verified end-to-end, including
        // the exact node-kind counts Task A's acceptance run established for this fixture.
        string fixtureDir = Path.Combine(TestPaths.RepoRoot, "tests", "fixtures-ts", "frontend-fixture");
        string db = Path.Combine(Path.GetTempPath(), $"slnmap-ts-gap-{Guid.NewGuid():N}.db");

        var (exit, stdout, stderr) = RunCli("analyze-ts", fixtureDir, "--db", db);

        Assert.True(
            exit == 0,
            $"Expected `slnmap analyze-ts` to succeed (exit 0); got {exit}.\nstdout:\n{stdout}\nstderr:\n{stderr}");
        Assert.True(File.Exists(db), $"analyze-ts should have produced a database at {db}.");

        await using var store = new SqliteGraphStore(db);
        var graph = await store.LoadGraphAsync();
        Assert.Equal(5, graph.Nodes.Count(n => n.Kind == NodeKind.FrontendCallSite));
        Assert.Equal(6, graph.Nodes.Count(n => n.Kind == NodeKind.UnresolvedCallSite));
    }

    [Fact]
    public void Sanity_ExpectedArtifactFixture_IsWellFormedAndMatchesDesignedSchema()
    {
        // Keeps tests/fixtures-ts/frontend-fixture/expected-callsites.json and this report's
        // schema description (§Q1/§Q2) from silently drifting apart before an implementation
        // exists to cross-check the fixture against. Passes today; it validates the fixture, not
        // a feature.
        string artifactPath = Path.Combine(
            TestPaths.RepoRoot, "tests", "fixtures-ts", "frontend-fixture", "expected-callsites.json");
        Assert.True(File.Exists(artifactPath), $"Expected artifact fixture not found at {artifactPath}");

        using var document = JsonDocument.Parse(File.ReadAllText(artifactPath));
        var root = document.RootElement;

        // schemaVersion 2 (Task B Part 0): spanStart/spanEnd added, additive over the
        // investigation's original schemaVersion 1.
        Assert.Equal(2, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("slnmap-ts", root.GetProperty("producer").GetString());

        var callSites = root.GetProperty("callSites");
        int resolved = 0, unresolved = 0;
        foreach (var callSite in callSites.EnumerateArray())
        {
            string kind = callSite.GetProperty("kind").GetString()!;
            Assert.True(
                kind is "FrontendCallSite" or "UnresolvedCallSite",
                $"Unexpected kind '{kind}' — the designed schema has exactly two call-site kinds.");

            Assert.True(callSite.TryGetProperty("verb", out _), "Every call site carries a verb (UNKNOWN when not statically known).");
            Assert.True(callSite.TryGetProperty("file", out _));
            Assert.True(callSite.TryGetProperty("line", out _));
            Assert.True(callSite.TryGetProperty("column", out _));
            Assert.True(callSite.TryGetProperty("spanStart", out _), "schemaVersion 2 requires spanStart.");
            Assert.True(callSite.TryGetProperty("spanEnd", out _), "schemaVersion 2 requires spanEnd.");

            if (kind == "FrontendCallSite")
            {
                resolved++;
                Assert.True(callSite.TryGetProperty("template", out _), "A resolved call site must carry its template.");
            }
            else
            {
                unresolved++;
                Assert.True(callSite.TryGetProperty("category", out var category), "An unresolved call site must carry a category code.");
                Assert.Contains(
                    category.GetString(),
                    new[]
                    {
                        "dynamic-base-url", "runtime-computed-segment", "non-constant-identifier",
                        "unrecognized-callee", "dynamic-import-or-indirection", "resolution-depth-exceeded",
                    });
                Assert.True(callSite.TryGetProperty("reason", out _), "An unresolved call site must carry a human-readable reason.");
            }
        }

        var stats = root.GetProperty("stats");
        Assert.Equal(resolved, stats.GetProperty("resolvedCount").GetInt32());
        Assert.Equal(unresolved, stats.GetProperty("unresolvedCount").GetInt32());
    }

    [Fact]
    public async Task Sanity_FrontendKindNames_RoundTripToTheirRealEnumValues()
    {
        // Task A's investigation-phase version of this test proved the OPPOSITE claim: before
        // NodeKind gained these members, inserting the raw kind-name strings degraded them to
        // NodeKind.Unknown (empirical proof no DDL migration was needed to add them). Now that
        // Task B has added NodeKind.FrontendCallSite/UnresolvedCallSite for real, that claim is
        // no longer true and the test's assertions inverted accordingly — the same round-trip
        // now proves the ADDITION worked: these kind names parse to their real enum values, not
        // Unknown. (The general degrade-gracefully mechanism itself, for whatever kind gets
        // added NEXT, is still proven separately and continuously by
        // SqliteGraphStoreTests.UnknownKindNames_DegradeToUnknown_InsteadOfCrashing, which uses
        // placeholder names unrelated to this change and is unaffected by it.)
        string directory = Path.Combine(Path.GetTempPath(), $"slnmap-ts-schema-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string dbPath = Path.Combine(directory, "graph.db");
            await using var store = new SqliteGraphStore(dbPath);
            await store.InitializeAsync();

            var connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString();
            await using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO nodes (id, kind, name, fqn, file, span_start, span_end)
                    VALUES ($id1, 'FrontendCallSite', '/UserTasks/assigned-tasks-with-summary',
                            'GET src/hooks/useUserTaskCenter.ts:17:12', 'src/hooks/useUserTaskCenter.ts', 0, 10),
                           ($id2, 'UnresolvedCallSite', 'dynamic-base-url: URL is built from an env value',
                            'UNRESOLVED dynamic-base-url src/services/dynamicService.ts:10:10',
                            'src/services/dynamicService.ts', 0, 10);
                    """;
                command.Parameters.AddWithValue("$id1", SymbolNode.CreateId(NodeKind.Unknown, "placeholder-1"));
                command.Parameters.AddWithValue("$id2", SymbolNode.CreateId(NodeKind.Unknown, "placeholder-2"));
                await command.ExecuteNonQueryAsync();
            }

            var loaded = await store.LoadGraphAsync();
            Assert.Equal(2, loaded.NodeCount);
            Assert.Contains(loaded.Nodes, n => n.Kind == NodeKind.FrontendCallSite);
            Assert.Contains(loaded.Nodes, n => n.Kind == NodeKind.UnresolvedCallSite);
            Assert.DoesNotContain(loaded.Nodes, n => n.Kind == NodeKind.Unknown);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup of a temp directory.
            }
        }
    }

    private static (int ExitCode, string Stdout, string Stderr) RunCli(params string[] args)
    {
        string config = AppContext.BaseDirectory.Replace('\\', '/')
            .Contains("/Release/", StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";
        string cliDll = Path.Combine(TestPaths.RepoRoot, "src", "Slnmap.Cli", "bin", config, "net9.0", "slnmap.dll");
        Assert.True(File.Exists(cliDll), $"CLI not built at {cliDll}");

        string workDir = Path.Combine(Path.GetTempPath(), $"slnmap-ts-cli-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        try
        {
            var psi = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = workDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add(cliDll);
            foreach (var arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start the slnmap CLI.");
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(60_000))
            {
                process.Kill(entireProcessTree: true);
                throw new InvalidOperationException("slnmap CLI timed out.");
            }

            return (process.ExitCode, stdout, stderr);
        }
        finally
        {
            try
            {
                Directory.Delete(workDir, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup of a temp directory.
            }
        }
    }
}
