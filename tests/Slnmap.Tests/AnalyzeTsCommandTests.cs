using System.Diagnostics;
using System.Linq;
using Xunit;

namespace Slnmap.Tests;

/// <summary>
/// `analyze-ts` verb error paths (reports/analyze-ts-verb-report.md Part 2/3), driven as a real
/// subprocess per the repo's CliErrorHandlingTests.cs convention: clean, actionable messages,
/// exit 1, never a stack trace. Node-absence and artifact-shape failures are simulated by
/// controlling the CHILD PROCESS's PATH — no test-only production hooks, no mocking.
/// </summary>
public sealed class AnalyzeTsCommandTests
{
    [Fact]
    public void MissingFrontendRoot_FailsCleanlyWithoutStackTrace()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"slnmap-ts-missing-root-{Guid.NewGuid():N}");
        var (exit, stdout, stderr) = RunCli("analyze-ts", missing, "--db", TempDbPath());

        Assert.Equal(1, exit);
        Assert.Contains("Frontend root not found", stderr, StringComparison.Ordinal);
        AssertNoStackTrace(stdout, stderr);
    }

    [Fact]
    public void MissingTsconfig_FailsCleanlyWithoutStackTrace()
    {
        string root = Path.Combine(Path.GetTempPath(), $"slnmap-ts-no-tsconfig-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var (exit, stdout, stderr) = RunCli("analyze-ts", root, "--db", TempDbPath());

            Assert.Equal(1, exit);
            Assert.Contains("tsconfig not found", stderr, StringComparison.Ordinal);
            Assert.Contains("--tsconfig", stderr, StringComparison.Ordinal);
            AssertNoStackTrace(stdout, stderr);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void NoNodeOnPath_FailsCleanlyWithTheInvestigationsExactMessage()
    {
        string fixtureDir = Path.Combine(TestPaths.RepoRoot, "tests", "fixtures-ts", "frontend-fixture");
        string pathWithoutNode = StripDirectoriesContaining("node");

        var (exit, stdout, stderr) = RunCli(
            ["analyze-ts", fixtureDir, "--db", TempDbPath()],
            environment: new Dictionary<string, string?> { ["PATH"] = pathWithoutNode });

        Assert.Equal(1, exit);
        Assert.Contains("Node.js not found.", stderr, StringComparison.Ordinal);
        Assert.Contains("Node 18+", stderr, StringComparison.Ordinal);
        Assert.Contains("https://nodejs.org", stderr, StringComparison.Ordinal);
        AssertNoStackTrace(stdout, stderr);
    }

    [Fact]
    public void MalformedArtifact_FailsCleanlyWithoutIngestingAnything()
    {
        using var fakeExtractor = FakeSlnmapTs.WritingRawArtifact("{ this is not valid json");
        string fixtureDir = Path.Combine(TestPaths.RepoRoot, "tests", "fixtures-ts", "frontend-fixture");
        string db = TempDbPath();

        var (exit, stdout, stderr) = RunCli(
            ["analyze-ts", fixtureDir, "--db", db],
            environment: fakeExtractor.PathPrependedEnvironment());

        Assert.Equal(1, exit);
        Assert.Contains("Malformed slnmap-ts artifact", stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(db), "A malformed artifact must never produce a partial ingest.");
        AssertNoStackTrace(stdout, stderr);
    }

    [Fact]
    public void SchemaVersion1Artifact_RejectedWithTheCorrectiveMessage()
    {
        string v1Artifact = """
            {
              "schemaVersion": 1,
              "producer": "slnmap-ts",
              "producerVersion": "0.1.0",
              "project": { "root": ".", "tsconfig": "tsconfig.json" },
              "stats": { "resolvedCount": 0, "unresolvedCount": 0, "coveragePercent": 100 },
              "callSites": []
            }
            """;
        using var fakeExtractor = FakeSlnmapTs.WritingRawArtifact(v1Artifact);
        string fixtureDir = Path.Combine(TestPaths.RepoRoot, "tests", "fixtures-ts", "frontend-fixture");
        string db = TempDbPath();

        var (exit, stdout, stderr) = RunCli(
            ["analyze-ts", fixtureDir, "--db", db],
            environment: fakeExtractor.PathPrependedEnvironment());

        Assert.Equal(1, exit);
        Assert.Contains("schemaVersion 1", stderr, StringComparison.Ordinal);
        Assert.Contains("slnmap-ts >= 0.2.0", stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(db));
        AssertNoStackTrace(stdout, stderr);
    }

    private static void AssertNoStackTrace(string stdout, string stderr)
    {
        string combined = $"{stdout}\n{stderr}";
        Assert.DoesNotContain("Unhandled exception", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", combined, StringComparison.Ordinal);
        Assert.DoesNotContain(".cs:line", combined, StringComparison.Ordinal);
    }

    private static string TempDbPath() =>
        Path.Combine(Path.GetTempPath(), $"slnmap-ts-verb-{Guid.NewGuid():N}.db");

    /// <summary>Removes every PATH entry containing an executable named <paramref name="baseName"/>
    /// — used to simulate "Node.js not found" without touching the real machine's PATH. Checks
    /// both the Windows (`.exe`) and Unix (bare name) forms unconditionally rather than branching
    /// on <see cref="OperatingSystem"/> — cheaper than getting the branch wrong, and this is
    /// exactly the kind of OS-specific detail that silently no-ops instead of failing loudly when
    /// missed (found on Ubuntu CI, reports/analyze-ts-verb-report.md's Part C incident: the
    /// original Windows-only "node.exe" check matched nothing on Linux, so PATH was never
    /// actually stripped and the test's real assertion never ran against the intended condition).</summary>
    private static string StripDirectoriesContaining(string baseName)
    {
        string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var kept = path
            .Split(Path.PathSeparator)
            .Where(dir => dir.Length == 0
                || (!File.Exists(Path.Combine(dir, baseName + ".exe")) && !File.Exists(Path.Combine(dir, baseName))));
        return string.Join(Path.PathSeparator, kept);
    }

    private static (int ExitCode, string Stdout, string Stderr) RunCli(params string[] args) =>
        RunCli(args, environment: null);

    private static (int ExitCode, string Stdout, string Stderr) RunCli(
        IReadOnlyList<string> args, IReadOnlyDictionary<string, string?>? environment)
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

            if (environment is not null)
            {
                foreach (var (key, value) in environment)
                {
                    if (value is null)
                    {
                        psi.EnvironmentVariables.Remove(key);
                    }
                    else
                    {
                        psi.EnvironmentVariables[key] = value;
                    }
                }
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

/// <summary>
/// A fake `slnmap-ts` PATH shim: a temp directory holding a `slnmap-ts.cmd`/`slnmap-ts` script
/// (via `node`, always genuinely present in these tests — only the EXTRACTOR is faked, not
/// Node itself) that writes a fixed, caller-supplied string to whatever `--out &lt;path&gt;` it
/// is invoked with, ignoring every other argument. Lets artifact-shape failure paths
/// (malformed JSON, schemaVersion 1) be exercised as real subprocess/PATH-resolution tests
/// rather than unit tests of TsArtifactFacts.Parse — analyze-ts's own PATH-first lookup
/// (reports/analyze-ts-verb-report.md Part 2) finds this shim before any real, globally-linked
/// `slnmap-ts`, exactly the same technique <see cref="AnalyzeTsCommandTests.NoNodeOnPath_FailsCleanlyWithTheInvestigationsExactMessage"/>
/// uses for Node.
/// </summary>
public sealed class FakeSlnmapTs : IDisposable
{
    private readonly string _directory;
    private readonly string _artifactContent;

    private FakeSlnmapTs(string directory, string artifactContent)
    {
        _directory = directory;
        _artifactContent = artifactContent;
    }

    public static FakeSlnmapTs WritingRawArtifact(string artifactContent)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"slnmap-ts-fake-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        File.WriteAllText(Path.Combine(directory, "fake-extract.mjs"), """
            import fs from 'node:fs';
            const args = process.argv.slice(2);
            const outIndex = args.indexOf('--out');
            const outPath = args[outIndex + 1];
            fs.writeFileSync(outPath, process.env.FAKE_SLNMAP_TS_ARTIFACT ?? '{}');
            """);

        // Windows finds this shim via its own PATHEXT-aware resolution (a bare ".cmd" is enough);
        // Unix needs a same-named, no-extension, executable-bit-set script instead — ResolveOnPath
        // (src/Slnmap.Cli/Program.cs) looks for the literal name "slnmap-ts" with no extension on
        // non-Windows. Writing both unconditionally (rather than branching on the current OS) is
        // exactly the same "don't guess which platform's rule applies" fix as
        // StripDirectoriesContaining above — the original single-shim version only worked on
        // Windows and silently never got resolved on Ubuntu CI, so the intended artifact-shape
        // failure never actually happened; the CLI fell through to a real, live npx pull instead.
        File.WriteAllText(Path.Combine(directory, "slnmap-ts.cmd"), """
            @echo off
            node "%~dp0fake-extract.mjs" %*
            """);

        string unixShimPath = Path.Combine(directory, "slnmap-ts");
        File.WriteAllText(unixShimPath, "#!/bin/sh\nexec node \"$(dirname \"$0\")/fake-extract.mjs\" \"$@\"\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                unixShimPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        return new FakeSlnmapTs(directory, artifactContent);
    }

    /// <summary>PATH with this shim's directory prepended, plus the artifact content the shim
    /// writes on invocation — passed to the child process as environment overrides.</summary>
    public IReadOnlyDictionary<string, string?> PathPrependedEnvironment()
    {
        string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        return new Dictionary<string, string?>
        {
            ["PATH"] = _directory + Path.PathSeparator + path,
            ["FAKE_SLNMAP_TS_ARTIFACT"] = _artifactContent,
        };
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of a temp directory.
        }
    }
}
