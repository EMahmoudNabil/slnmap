namespace Fixture.App;

// Exercises LIE 2 (v0.6.1): this anonymous type is structurally IDENTICAL to the one in
// FixtureCli/AnonymousReport.cs. Their FQNs render identically ("<anonymous type: ...>"), so
// without a fix the two collapse into one node pinned to whichever file analyzed first —
// fabricating a cross-project References edge between two projects that share no real code.
public static class AppReport
{
    public static string Build()
    {
        var row = new { Id = 1, Label = "app" };
        return row.Label;
    }

    // Named tuple elements are the same defect class: `(int First, int Second)` here and in
    // FixtureCli render identical element FQNs ("(int First, int Second).First"), so element
    // nodes would collapse across projects exactly like the anonymous type above.
    public static int Sum()
    {
        var pair = (First: 1, Second: 2);
        return pair.First + pair.Second;
    }
}
