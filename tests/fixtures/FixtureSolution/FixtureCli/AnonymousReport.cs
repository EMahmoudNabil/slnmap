namespace Fixture.Cli;

// The FixtureApp twin of this file declares a structurally identical anonymous type — see
// FixtureApp/AnonymousReport.cs for why. This file must add NO dependency on any other project
// (FixtureCli's dependencies stay disjoint from FixtureApp's for the incremental tests).
public static class CliReport
{
    public static string Build()
    {
        var row = new { Id = 1, Label = "cli" };
        return row.Label;
    }

    // Structurally identical to FixtureApp's tuple — see the comment there.
    public static int Sum()
    {
        var pair = (First: 1, Second: 2);
        return pair.First + pair.Second;
    }
}
