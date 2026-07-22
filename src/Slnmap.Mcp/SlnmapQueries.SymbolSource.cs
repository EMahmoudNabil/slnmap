using System.Globalization;
using System.Text;
using Slnmap.Core.Graph;

namespace Slnmap.Mcp;

public sealed partial class SlnmapQueries
{
    private const int SourceMaxLines = 120;
    private const int SourceMaxContext = 20;

    /// <summary>
    /// Returns the real source of a symbol, read from its file at the stored declaration span and
    /// expanded by <paramref name="contextLines"/> on each side. Capped at 120 lines. Needs no schema
    /// change: spans are already persisted.
    /// </summary>
    public async Task<string> GetSymbolSourceAsync(string fqn, int contextLines, CancellationToken cancellationToken = default)
    {
        if (await NotAnalyzedAsync(cancellationToken).ConfigureAwait(false) is { } notReady)
        {
            return notReady;
        }

        var matches = await _store.GetNodesByFqnAsync(fqn, cancellationToken).ConfigureAwait(false);
        if (matches.Count == 0)
        {
            return await NotFoundAsync(fqn, cancellationToken).ConfigureAwait(false);
        }

        var node = matches.FirstOrDefault(m => m.FilePath is not null && m.Span is not null) ?? matches[0];
        if (node.FilePath is not { } file || node.Span is not { } span)
        {
            return $"{node.Fqn} ({node.Kind}) has no single source location (projects, namespaces and partial types span zero or many files).";
        }

        if (!File.Exists(file))
        {
            return $"Source file not found at {file}; it may have moved or been deleted since analysis — re-run 'slnmap analyze'.";
        }

        string text;
        try
        {
            text = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"Could not read source file {file}: {e.Message}";
        }

        int context = Math.Clamp(contextLines, 0, SourceMaxContext);
        var allLines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        int startLine = LineOfOffset(text, span.Start);
        int endLine = LineOfOffset(text, Math.Min(span.End, text.Length));

        int from = Math.Max(1, startLine - context);
        int to = Math.Min(allLines.Length, endLine + context);
        bool capped = to - from + 1 > SourceMaxLines;
        if (capped)
        {
            to = from + SourceMaxLines - 1;
        }

        int width = to.ToString(CultureInfo.InvariantCulture).Length;
        var builder = new StringBuilder();
        builder.AppendLine(node.Fqn);
        builder.AppendLine($"{file}:{startLine} ({node.Kind}, +/-{context} context lines)");
        for (int i = from; i <= to; i++)
        {
            builder.AppendLine($"{i.ToString(CultureInfo.InvariantCulture).PadLeft(width)} | {allLines[i - 1]}");
        }

        if (capped)
        {
            builder.AppendLine($"... snippet capped at {SourceMaxLines} lines — open the file for the full declaration.");
        }

        return builder.ToString().TrimEnd();
    }

    private static int LineOfOffset(string text, int offset)
    {
        int clamped = Math.Clamp(offset, 0, text.Length);
        int line = 1;
        for (int i = 0; i < clamped; i++)
        {
            if (text[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }
}
