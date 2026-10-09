using System.Text;
using System.Text.Json;

namespace Fixture.Lib.External;

// v0.14.0 fixture for find_callers_of_external (docs/EXPANSION-SPECS.md §7).

public sealed class ExternalCaller
{
    // A field initializer: the call is attributed to the field (fields are nodes).
    private static readonly StringBuilder Shared = new();

    /// <summary>One constructor, Append(string) twice (one fact, count 2), Append(char), ToString.</summary>
    public string Build()
    {
        var builder = new StringBuilder();
        builder.Append("a");
        builder.Append("b");
        builder.Append('c');
        return builder.ToString() + Shared.Length;
    }

    /// <summary>A generic framework method in a different assembly (System.Text.Json).</summary>
    public string Serialize(object value) => JsonSerializer.Serialize(value);

    /// <summary>An in-source call — never an external call.</summary>
    public int Local() => Helper();

    private static int Helper() => 1;
}
