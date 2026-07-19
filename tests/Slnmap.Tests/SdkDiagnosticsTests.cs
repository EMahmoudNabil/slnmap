using Slnmap.Analysis;
using Xunit;

namespace Slnmap.Tests;

public sealed class SdkResolutionDiagnosticsTests
{
    [Theory]
    [InlineData("Error running dotnet: hostfxr_resolve_sdk2 failed")]
    [InlineData("A compatible .NET SDK was not found.")]
    [InlineData("Requested SDK version 10.0.100 from global.json was not found")]
    public void IsSdkResolutionFailure_RecognizesSignatures(string message)
    {
        Assert.True(SdkResolutionDiagnostics.IsSdkResolutionFailure(new InvalidOperationException(message)));
    }

    [Fact]
    public void IsSdkResolutionFailure_ChecksInnerExceptions()
    {
        var inner = new Exception("native error hostfxr_resolve_sdk2 (0x8000...)");
        Assert.True(SdkResolutionDiagnostics.IsSdkResolutionFailure(new Exception("workspace open failed", inner)));
    }

    [Fact]
    public void IsSdkResolutionFailure_IgnoresUnrelatedErrors()
    {
        Assert.False(SdkResolutionDiagnostics.IsSdkResolutionFailure(new Exception("file not found: Foo.cs")));
    }

    [Fact]
    public void BuildMessage_WithRequirement_IsTwoActionableLines()
    {
        var requirement = new SdkRequirement("/repo/global.json", "10.0.100", "latestPatch");
        string message = SdkResolutionDiagnostics.BuildMessage("/repo/App.sln", requirement, ["9.0.314"]);

        var lines = message.Split(Environment.NewLine);
        Assert.Equal(2, lines.Length);
        Assert.Contains("10.0.100", lines[0], StringComparison.Ordinal); // required
        Assert.Contains("9.0.314", lines[0], StringComparison.Ordinal);  // installed
        Assert.Contains("global.json", lines[0], StringComparison.Ordinal);
        Assert.Contains("slnmap doctor", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void BuildMessage_WithoutRequirement_StillActionable()
    {
        string message = SdkResolutionDiagnostics.BuildMessage("/repo/App.sln", requirement: null, installedVersions: []);
        Assert.Contains("none", message, StringComparison.Ordinal);
        Assert.Contains("slnmap doctor", message, StringComparison.Ordinal);
    }
}

public sealed class GlobalJsonTests
{
    private static string WriteGlobalJson(string contents)
    {
        string dir = Path.Combine(Path.GetTempPath(), "slnmap-gj", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "global.json"), contents);
        return dir;
    }

    [Fact]
    public void FindSdkRequirement_ReadsVersionAndRollForward()
    {
        string dir = WriteGlobalJson("""{ "sdk": { "version": "10.0.100", "rollForward": "latestMinor" } }""");
        try
        {
            var requirement = GlobalJson.FindSdkRequirement(dir);
            Assert.NotNull(requirement);
            Assert.Equal("10.0.100", requirement!.Version);
            Assert.Equal("latestMinor", requirement.RollForward);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void FindSdkRequirement_WalksUpFromSubdirectory()
    {
        string dir = WriteGlobalJson("""{ "sdk": { "version": "9.0.300" } }""");
        try
        {
            string nested = Path.Combine(dir, "src", "App");
            Directory.CreateDirectory(nested);
            var requirement = GlobalJson.FindSdkRequirement(nested);
            Assert.NotNull(requirement);
            Assert.Equal("9.0.300", requirement!.Version);
            Assert.Equal("latestPatch", requirement.RollForward); // default when unspecified
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void FindSdkRequirement_NoPin_ReturnsNull()
    {
        string dir = WriteGlobalJson("""{ "msbuild-sdks": { "X": "1.0.0" } }"""); // no sdk.version
        try
        {
            Assert.Null(GlobalJson.FindSdkRequirement(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    // pinned, rollForward, installed, expected
    [InlineData("10.0.100", "latestPatch", "9.0.314", false)] // lower major never satisfies a higher pin
    [InlineData("9.0.300", "latestPatch", "9.0.314", true)]   // same feature band, higher patch
    [InlineData("9.0.100", "latestPatch", "9.0.314", false)]  // different feature band (1xx vs 3xx)
    [InlineData("9.0.314", "latestPatch", "9.0.305", true)]   // latestPatch takes any patch in the band
    [InlineData("9.0.314", "patch", "9.0.305", false)]        // patch needs an equal-or-higher patch
    [InlineData("9.0.305", "patch", "9.0.314", true)]         // higher patch in the pinned band
    [InlineData("9.0.314", "feature", "9.0.305", true)]       // feature: any patch in the pinned band
    [InlineData("9.0.100", "feature", "9.0.314", true)]       // feature: higher band, same major.minor
    [InlineData("9.0.314", "feature", "8.0.400", false)]      // feature stays within the major.minor
    [InlineData("9.0.100", "latestMinor", "9.0.314", true)]   // minor policy rolls within the major
    [InlineData("9.0.314", "disable", "9.0.314", true)]       // exact match
    [InlineData("9.0.100", "disable", "9.0.314", false)]      // disable requires exact
    [InlineData("8.0.100", "major", "9.0.314", true)]         // major policy rolls across majors
    public void IsSatisfied_RespectsRollForward(string pinned, string rollForward, string installed, bool expected)
    {
        var requirement = new SdkRequirement("/global.json", pinned, rollForward);
        Assert.Equal(expected, GlobalJson.IsSatisfied(requirement, [installed]));
    }
}
