using System.Text.Json;
using Microsoft.CodeAnalysis;

namespace Slnmap.Analysis;

/// <summary>
/// Detects a project that MSBuildWorkspace loaded without its dependencies (v0.14.0). An
/// unrestored project still loads and compiles, with no workspace diagnostic, so its unresolved
/// types fail silently. What is missing depends on the SDK: under SDK 10 it has no metadata
/// references at all; under SDK 9 the framework targeting packs still resolve and only the NuGet
/// package references are missing (found by the Linux CI leg, where SDK 9 is the only SDK).
/// Confirmed on blazor-workshop (0 endpoints before <c>dotnet restore</c>, 6 after) and on
/// eShopOnContainers under SDK 10 (its restore fails with NU1504). The check is cheap, runs on
/// every project on every run, and never guesses: only an empty reference set, a missing assets
/// file on an SDK-style project, or an error the restore itself recorded counts.
/// </summary>
public static class ProjectRestoreCheck
{
    private const string AssetsFileName = "project.assets.json";
    private const string GeneratedEditorConfigSuffix = ".GeneratedMSBuildEditorConfig.editorconfig";

    /// <summary>Why <paramref name="project"/> is incomplete, or null when its dependencies resolved.</summary>
    public static string? Diagnose(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        string? assets = project.FilePath is null ? null : FindAssetsFile(project);
        if (assets is not null && AssetsErrorsAt(assets) is { } errors)
        {
            return $"restore recorded errors ({errors}); package types may be unresolved";
        }

        if (project.MetadataReferences.Count == 0)
        {
            return "no references resolved; restore never ran or failed";
        }

        // SDK-style projects always get an assets file from restore, even with no packages; a
        // legacy (non-SDK) project never does, so its absence means nothing there.
        if (assets is null && project.FilePath is { } path && IsSdkStyle(path))
        {
            return "not restored (no project.assets.json); NuGet package types are unresolved";
        }

        return null;
    }

    /// <summary>
    /// The project's restore output, or null when none exists. Looks in the default
    /// <c>obj/</c> folder, then walks up from the generated MSBuild editorconfig the design-time
    /// build writes into the intermediate folder, so a relocated layout (e.g.
    /// <c>UseArtifactsOutput</c>'s <c>artifacts/obj/&lt;project&gt;/</c>) is found too.
    /// </summary>
    internal static string? FindAssetsFile(Project project)
    {
        var candidates = new List<string>();
        if (Path.GetDirectoryName(project.FilePath) is { } projectDirectory)
        {
            candidates.Add(Path.Combine(projectDirectory, "obj"));
        }

        foreach (var config in project.AnalyzerConfigDocuments)
        {
            if (config.FilePath is { } configPath
                && configPath.EndsWith(GeneratedEditorConfigSuffix, StringComparison.OrdinalIgnoreCase))
            {
                // obj/<configuration>/<tfm>/ -> obj/ is two levels up; allow one more for layouts
                // that add a level.
                var directory = Path.GetDirectoryName(configPath);
                for (int level = 0; level < 4 && directory is not null; level++)
                {
                    candidates.Add(directory);
                    directory = Path.GetDirectoryName(directory);
                }
            }
        }

        foreach (string candidate in candidates)
        {
            string assets = Path.Combine(candidate, AssetsFileName);
            if (File.Exists(assets))
            {
                return assets;
            }
        }

        return null;
    }

    /// <summary>True when the project file uses an MSBuild SDK (<c>&lt;Project Sdk=…&gt;</c> or an <c>&lt;Sdk&gt;</c> element).</summary>
    internal static bool IsSdkStyle(string projectFilePath)
    {
        try
        {
            string text = File.ReadAllText(projectFilePath);
            return text.Contains("Sdk=\"", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Sdk='", StringComparison.OrdinalIgnoreCase)
                || text.Contains("<Sdk ", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// The distinct error codes NuGet recorded in the project's default
    /// <c>obj/project.assets.json</c> (comma-separated), or null when the file is absent,
    /// unreadable, or has no error entries.
    /// </summary>
    public static string? AssetsErrors(string projectFilePath)
    {
        string? directory = Path.GetDirectoryName(projectFilePath);
        return directory is null ? null : AssetsErrorsAt(Path.Combine(directory, "obj", AssetsFileName));
    }

    /// <summary>The distinct error codes recorded in the assets file at <paramref name="assetsPath"/>; see <see cref="AssetsErrors"/>.</summary>
    public static string? AssetsErrorsAt(string assetsPath)
    {
        try
        {
            if (!File.Exists(assetsPath))
            {
                return null;
            }

            // Most assets files carry no "logs" section at all; skip the parse for those (large
            // solutions have many multi-megabyte assets files, and this runs on every analyze).
            byte[] bytes = File.ReadAllBytes(assetsPath);
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
