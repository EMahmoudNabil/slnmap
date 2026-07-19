using System.Diagnostics;
using Xunit;

namespace Slnmap.Tests;

/// <summary>
/// Drives the built `slnmap` CLI as a process to verify that a bad analyze target fails with a clean,
/// actionable message and exit code 1 — never an unhandled exception or a stack trace (v0.1.8 fix).
/// </summary>
public sealed class CliErrorHandlingTests
{
    [Fact]
    public void Analyze_NonexistentPath_FailsCleanlyWithoutStackTrace()
    {
        string missing = Path.Combine(
            Path.GetTempPath(), $"slnmap-missing-{Guid.NewGuid():N}.sln");

        var (exit, stdout, stderr) = RunCli("analyze", missing);

        Assert.Equal(1, exit);
        Assert.Contains("Solution or project file not found", stderr, StringComparison.Ordinal);
        AssertNoStackTrace(stdout, stderr);
    }

    [Fact]
    public void Analyze_NonexistentPath_WithVerbose_IncludesFullExceptionForBugReports()
    {
        string missing = Path.Combine(
            Path.GetTempPath(), $"slnmap-missing-{Guid.NewGuid():N}.sln");

        var (exit, stdout, stderr) = RunCli("analyze", missing, "--verbose");

        Assert.Equal(1, exit);
        Assert.Contains("Solution or project file not found", stderr, StringComparison.Ordinal);

        // --verbose is the diagnostics escape hatch: the full exception (type + stack) is present.
        string combined = $"{stdout}\n{stderr}";
        Assert.Contains("FileNotFoundException", combined, StringComparison.Ordinal);
        Assert.Contains("   at ", combined, StringComparison.Ordinal);
    }

    [Fact]
    public void Analyze_DirectoryInsteadOfSolution_FailsCleanlyWithoutStackTrace()
    {
        // A directory is not a .sln/.csproj; the tool must reject it as gracefully as a missing file.
        var (exit, stdout, stderr) = RunCli("analyze", TestPaths.RepoRoot);

        Assert.Equal(1, exit);
        Assert.Contains("Solution or project file not found", stderr, StringComparison.Ordinal);
        AssertNoStackTrace(stdout, stderr);
    }

    private static void AssertNoStackTrace(string stdout, string stderr)
    {
        string combined = $"{stdout}\n{stderr}";
        Assert.DoesNotContain("Unhandled exception", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", combined, StringComparison.Ordinal);   // CLR stack frame lines
        Assert.DoesNotContain(".cs:line", combined, StringComparison.Ordinal);  // internal source references
    }

    private static (int ExitCode, string Stdout, string Stderr) RunCli(params string[] args)
    {
        string config = AppContext.BaseDirectory.Replace('\\', '/')
            .Contains("/Release/", StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";
        string cliDll = Path.Combine(TestPaths.RepoRoot, "src", "Slnmap.Cli", "bin", config, "net9.0", "slnmap.dll");
        Assert.True(File.Exists(cliDll), $"CLI not built at {cliDll}");

        // Run in a throwaway directory so a stray default slnmap.db never lands in the repo.
        string workDir = Path.Combine(Path.GetTempPath(), $"slnmap-cli-{Guid.NewGuid():N}");
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
