using Slnmap.Cli;
using Xunit;

namespace Slnmap.Tests;

/// <summary>
/// The watch loop's event classification. The database exclusion is the load-bearing case: the
/// default db path sits inside the watched tree, so without it every save would re-trigger the
/// watcher in an endless loop.
/// </summary>
public sealed class WatchFilterTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "watch-root");
    private readonly WatchFilter _filter = new(Path.Combine(Root, "slnmap.db"));

    // Inline data is written with '\'; real events arrive with the platform's separator.
    private static string Platform(string relative) => relative.Replace('\\', Path.DirectorySeparatorChar);

    [Theory]
    [InlineData("slnmap.db")]
    [InlineData("slnmap.db-wal")]
    [InlineData("slnmap.db-shm")]
    [InlineData("slnmap.db.tmp")]
    public void TheDatabaseAndItsSidecars_AreIgnored(string name) =>
        Assert.Equal(WatchVerdict.Ignore, _filter.Classify(Path.Combine(Root, name)));

    [Theory]
    [InlineData(@"src\Lib\bin\Debug\net9.0\Lib.cs")]
    [InlineData(@"src\Lib\obj\project.assets.json")]
    [InlineData(@".git\index.lock")]
    [InlineData(@".vs\solution\cache.cs")]
    [InlineData(@"node_modules\pkg\file.cs")]
    public void BuildOutputsAndVcsInternals_AreIgnored(string relative) =>
        Assert.Equal(WatchVerdict.Ignore, _filter.Classify(Path.Combine(Root, Platform(relative))));

    [Theory]
    [InlineData(@"src\Lib\Shapes.cs")]
    [InlineData(@"src\Lib\SHAPES.CS")]
    public void CSharpSources_AreContent(string relative) =>
        Assert.Equal(WatchVerdict.Content, _filter.Classify(Path.Combine(Root, Platform(relative))));

    [Theory]
    [InlineData(@"src\Lib\Lib.csproj")]
    [InlineData(@"My.sln")]
    [InlineData(@"Directory.Build.props")]
    [InlineData(@"Directory.Packages.props")]
    [InlineData(@"src\Lib\Lib.targets")]
    [InlineData(@"global.json")]
    public void SolutionShapeFiles_AreStructural(string relative) =>
        Assert.Equal(WatchVerdict.Structural, _filter.Classify(Path.Combine(Root, Platform(relative))));

    [Theory]
    [InlineData(@"README.md")]
    [InlineData(@"src\Lib\appsettings.json")]
    [InlineData(@"notes.txt")]
    public void UnrelatedFiles_AreIgnored(string relative) =>
        Assert.Equal(WatchVerdict.Ignore, _filter.Classify(Path.Combine(Root, Platform(relative))));
}
