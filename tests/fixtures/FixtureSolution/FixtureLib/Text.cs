namespace Fixture.Lib;

/// <summary>FixtureCli's only dependency — see the note in FixtureCli/Program.cs.</summary>
public static class Labels
{
    public static string For(double area) => $"area={area:F1}";
}
