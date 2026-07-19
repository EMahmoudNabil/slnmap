using Slnmap.Analysis;
using Xunit;

namespace Slnmap.Tests;

public sealed class EnvironmentDoctorTests
{
    [Fact]
    public void EvaluateSdks_DotnetMissing_FailsWithInstallFix()
    {
        var check = EnvironmentDoctor.EvaluateSdks(dotnetFound: false, exitCode: -1, stdout: string.Empty);
        Assert.False(check.Ok);
        Assert.Contains("PATH", check.Detail, StringComparison.Ordinal);
        Assert.Contains("dotnet.microsoft.com", check.Fix!, StringComparison.Ordinal);
    }

    [Fact]
    public void EvaluateSdks_NoSdksListed_Fails()
    {
        var check = EnvironmentDoctor.EvaluateSdks(dotnetFound: true, exitCode: 0, stdout: "\n");
        Assert.False(check.Ok);
        Assert.Contains("Install the .NET SDK", check.Fix!, StringComparison.Ordinal);
    }

    [Fact]
    public void EvaluateSdks_WithSdks_ReportsNewest()
    {
        const string output = "8.0.404 [C:\\Program Files\\dotnet\\sdk]\n9.0.314 [C:\\Program Files\\dotnet\\sdk]\n";
        var check = EnvironmentDoctor.EvaluateSdks(dotnetFound: true, exitCode: 0, stdout: output);
        Assert.True(check.Ok);
        Assert.Contains("2 SDK(s)", check.Detail, StringComparison.Ordinal);
        Assert.Contains("9.0.314", check.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckGraphDirectory_WritablePath_Ok()
    {
        string dir = Path.Combine(Path.GetTempPath(), "slnmap-doctor", Guid.NewGuid().ToString("N"));
        try
        {
            var check = EnvironmentDoctor.CheckGraphDirectory(Path.Combine(dir, "graph.db"));
            Assert.True(check.Ok);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void CheckGraphDirectory_DirectoryPathIsAFile_Fails()
    {
        // A regular file cannot be a parent directory, so creating the graph dir must fail cleanly.
        string file = Path.Combine(Path.GetTempPath(), $"slnmap-doctor-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(file, "not a directory");
        try
        {
            var check = EnvironmentDoctor.CheckGraphDirectory(Path.Combine(file, "graph.db"));
            Assert.False(check.Ok);
            Assert.NotNull(check.Fix);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task RunAsync_OnThisEnvironment_ReturnsAllChecks()
    {
        string dir = Path.Combine(Path.GetTempPath(), "slnmap-doctor", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var checks = await EnvironmentDoctor.RunAsync(Path.Combine(dir, "graph.db"), dir);
            Assert.Equal(4, checks.Count);
            // The build/test host has an SDK and a writable temp dir with no global.json, so these pass.
            Assert.True(checks.Single(c => c.Name == ".NET SDK").Ok);
            Assert.True(checks.Single(c => c.Name == "Graph directory").Ok);
            Assert.True(checks.Single(c => c.Name == "global.json SDK").Ok);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void CheckGlobalJsonSdk_UninstalledPin_Fails()
    {
        string dir = Path.Combine(Path.GetTempPath(), "slnmap-doctor-pin", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "global.json"), """{ "sdk": { "version": "10.0.100" } }""");
            var check = EnvironmentDoctor.CheckGlobalJsonSdk(dir, ["9.0.314"]);
            Assert.False(check.Ok);
            Assert.Contains("10.0.100", check.Detail, StringComparison.Ordinal);
            Assert.NotNull(check.Fix);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void CheckGlobalJsonSdk_NoPin_Ok()
    {
        string dir = Path.Combine(Path.GetTempPath(), "slnmap-doctor-nopin", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var check = EnvironmentDoctor.CheckGlobalJsonSdk(dir, ["9.0.314"]);
            Assert.True(check.Ok);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
