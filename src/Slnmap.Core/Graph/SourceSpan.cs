using System.Globalization;

namespace Slnmap.Core.Graph;

/// <summary>A half-open character range <c>[Start, End)</c> within a source file.</summary>
public readonly record struct SourceSpan
{
    public SourceSpan(int start, int end)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfLessThan(end, start);
        Start = start;
        End = end;
    }

    public int Start { get; }

    public int End { get; }

    public int Length => End - Start;

    /// <summary>Formats as <c>start-end</c>, the representation used by graph stores.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Start}-{End}");

    public static SourceSpan Parse(string value)
    {
        if (!TryParse(value, out var span))
        {
            throw new FormatException($"'{value}' is not a valid span; expected 'start-end'.");
        }

        return span;
    }

    public static bool TryParse(string? value, out SourceSpan span)
    {
        span = default;
        if (value is null)
        {
            return false;
        }

        int separator = value.IndexOf('-', StringComparison.Ordinal);
        if (separator <= 0
            || !int.TryParse(value.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out int start)
            || !int.TryParse(value.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int end)
            || end < start)
        {
            return false;
        }

        span = new SourceSpan(start, end);
        return true;
    }
}
