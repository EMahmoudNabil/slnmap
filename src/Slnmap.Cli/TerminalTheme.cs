using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

/// <summary>
/// Minimal semantic ANSI colouring for the CLI, modelled on dotnet/git conventions — plain coloured
/// text, never tables or box-drawing. Colour is emitted only to a real terminal: it is suppressed when
/// <c>NO_COLOR</c> is set, when <c>TERM=dumb</c>, when the stream is redirected (piped / file / tests),
/// or when Windows virtual-terminal mode cannot be enabled. When suppressed, every method returns its
/// input unchanged, so output is plain text with identical characters.
/// </summary>
internal sealed class Palette
{
    private const char Esc = (char)0x1b; // ANSI escape; built from its code so no control char sits in source.
    private static readonly string Reset = $"{Esc}[0m";

    /// <summary>Palette for stdout (the analyze summary and status blocks).</summary>
    public static Palette Out { get; } = new(ColorEnabled(stdout: true));

    /// <summary>Palette for stderr (warnings and error messages).</summary>
    public static Palette Err { get; } = new(ColorEnabled(stdout: false));

    private readonly bool _color;

    private Palette(bool color) => _color = color;

    /// <summary>Constructs a palette with colour forced on or off — for tests, which have no real TTY.</summary>
    internal static Palette CreateForTests(bool color) => new(color);

    public string Label(string s) => Wrap(s, "90");    // dim grey — field labels
    public string Number(string s) => Wrap(s, "97");   // bright white — counts
    public string Type(string s) => Wrap(s, "36");     // teal — node/edge kind names
    public string Warn(string s) => Wrap(s, "33");     // yellow — warnings
    public string Error(string s) => Wrap(s, "31");    // red — errors
    public string Success(string s) => Wrap(s, "32");  // green — success

    private string Wrap(string s, string code) => _color ? $"{Esc}[{code}m{s}{Reset}" : s;

    private static bool ColorEnabled(bool stdout)
    {
        if (Environment.GetEnvironmentVariable("NO_COLOR") is not null)
        {
            return false;
        }

        if (string.Equals(Environment.GetEnvironmentVariable("TERM"), "dumb", StringComparison.Ordinal))
        {
            return false;
        }

        if (stdout ? Console.IsOutputRedirected : Console.IsErrorRedirected)
        {
            return false;
        }

        return EnableWindowsVirtualTerminal(stdout);
    }

    /// <summary>POSIX terminals interpret ANSI natively; a Windows console needs VT mode turned on first.</summary>
    private static bool EnableWindowsVirtualTerminal(bool stdout)
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        try
        {
            nint handle = GetStdHandle(stdout ? StdOutputHandle : StdErrorHandle);
            if (handle == 0 || handle == -1)
            {
                return false;
            }

            if (!GetConsoleMode(handle, out uint mode))
            {
                return false;
            }

            return (mode & EnableVirtualTerminalProcessing) != 0
                || SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    private const int StdOutputHandle = -11;
    private const int StdErrorHandle = -12;
    private const uint EnableVirtualTerminalProcessing = 0x0004;

    // Classic DllImport (not LibraryImport) is intentional: LibraryImport's source generator needs
    // AllowUnsafeBlocks, which isn't worth enabling project-wide for three console-mode calls.
#pragma warning disable SYSLIB1054
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetConsoleMode(nint hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleMode(nint hConsoleHandle, uint dwMode);
#pragma warning restore SYSLIB1054
}

/// <summary>Small text-layout helpers for the CLI: friendly timestamps and wrapped lists.</summary>
internal static class CliFormat
{
    /// <summary>
    /// Formats a stored ISO timestamp as e.g. "18 Jul 2026, 06:52 UTC — 3h ago". Falls back to the raw
    /// string if it cannot be parsed (so a non-timestamp meta value is shown as-is rather than dropped).
    /// </summary>
    public static string FriendlyTimestamp(string raw)
    {
        if (!DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when))
        {
            return raw;
        }

        var utc = when.ToUniversalTime();
        return $"{utc.ToString("dd MMM yyyy, HH:mm", CultureInfo.InvariantCulture)} UTC — {Relative(utc)}";
    }

    private static string Relative(DateTimeOffset utc)
    {
        var age = DateTimeOffset.UtcNow - utc;
        if (age < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        if (age < TimeSpan.FromHours(1))
        {
            return $"{(int)age.TotalMinutes}m ago";
        }

        if (age < TimeSpan.FromDays(1))
        {
            return $"{(int)age.TotalHours}h ago";
        }

        if (age < TimeSpan.FromDays(30))
        {
            return $"{(int)age.TotalDays}d ago";
        }

        return utc.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Wraps a comma-separated list to the terminal width, each line indented by <paramref name="indent"/>
    /// spaces (a clean hanging block under its label). Operates on plain text — callers colour whole lines,
    /// not individual items — so width is measured accurately.
    /// </summary>
    public static IReadOnlyList<string> WrapList(IReadOnlyList<string> items, int indent, int width)
    {
        var lines = new List<string>();
        if (items.Count == 0)
        {
            return lines;
        }

        string pad = new(' ', indent);
        var line = new StringBuilder(pad);
        for (int i = 0; i < items.Count; i++)
        {
            string token = i < items.Count - 1 ? items[i] + "," : items[i];
            bool empty = line.Length == pad.Length;
            int cost = (empty ? 0 : 1) + token.Length;
            if (!empty && line.Length + cost > width)
            {
                lines.Add(line.ToString());
                line.Clear().Append(pad);
                empty = true;
            }

            if (!empty)
            {
                line.Append(' ');
            }

            line.Append(token);
        }

        lines.Add(line.ToString());
        return lines;
    }

    /// <summary>Usable console width, or 80 when it is unknown (redirected output) or implausibly small.</summary>
    public static int TerminalWidth()
    {
        try
        {
            int width = Console.WindowWidth;
            return width >= 40 ? width : 80;
        }
        catch (IOException)
        {
            return 80;
        }
    }
}
