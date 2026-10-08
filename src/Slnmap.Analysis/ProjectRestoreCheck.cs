using System.Text.Json;
using Microsoft.CodeAnalysis;

namespace Slnmap.Analysis;

/// <summary>
/// Detects a project that MSBuildWorkspace loaded without its dependencies (v0.14.0). An
/// unrestored project still loads and compiles — with no metadata references at all, and with no
/// workspace diagnostic — so every framework and package type in it silently fails to resolve.
/// Confirmed on blazor-workshop (0 endpoints before <c>dotnet restore</c>, 6 after) and on
/// eShopOnContainers under SDK 10 (its restore fails with NU1504). The check is cheap, runs on
/// every project on every run, and never guesses: only an empty reference set or an error the
/// restore itself recorded counts.
/// </summary>
public static class ProjectRestoreCheck
{
    /// <summary>Why <paramref name="project"/> is incomplete, or null when its dependencies resolved.</summary>
    public static string? Diagnose(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.FilePath is { } path && AssetsErrors(path) is { } errors)
        {
            return $"restore recorded errors ({errors}); package types may be unresolved";
        }

        return project.MetadataReferences.Count == 0
            ? "no references resolved; restore never ran or failed"
            : null;
    }

    /// <summary>
    /// The distinct error codes NuGet recorded in the project's <c>obj/project.assets.json</c>
    /// (comma-separated), or null when the file is absent, unreadable, or has no error entries.
    /// Only the default intermediate path is checked; a relocated <c>obj</c> folder is simply not
    /// inspected (the empty-reference check still applies).
    /// </summary>
    public static string? AssetsErrors(string projectFilePath)
    {
        string? directory = Path.GetDirectoryName(projectFilePath);
        if (directory is null)
        {
            return null;
        }

        string assets = Path.Combine(directory, "obj", "project.assets.json");
        try
        {
            if (!File.Exists(assets))
            {
                return null;
            }

            // Most assets files carry no "logs" section at all; skip the parse for those (large
            // solutions have many multi-megabyte assets files, and this runs on every analyze).
            byte[] bytes = File.ReadAllBytes(assets);
            if (bytes.AsSpan().IndexOf("\"logs\""u8) < 0)
            {
                return null;
            }

            using var document = JsonDocument.Parse(bytes);
            if (!document.RootElement.TryGetProperty("logs", out var logs) || logs.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var codes = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var entry in logs.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Object
                    && entry.TryGetProperty("level", out var level)
                    && string.Equals(level.GetString(), "Error", StringComparison.OrdinalIgnoreCase))
                {
                    codes.Add(entry.TryGetProperty("code", out var code) && code.GetString() is { Length: > 0 } c ? c : "unknown");
                }
            }

            return codes.Count == 0 ? null : string.Join(", ", codes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
