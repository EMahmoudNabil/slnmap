using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
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

        if (assets is not null && project.FilePath is { } projectFile && MissingPackages(projectFile, assets) is { Count: > 0 } missing)
        {
            return $"restore is out of date: {string.Join(", ", missing)} not restored yet; those package types are unresolved";
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

    /// <summary>
    /// Packages the project file itself declares that its assets file doesn't list: the restore
    /// predates them (v0.14.1). Only unconditional <c>&lt;PackageReference Include="…"&gt;</c>
    /// items with a literal name count — anything under a <c>Condition</c>, built from a property
    /// or item, or declared in an imported file is skipped, so a mismatch is never a guess.
    /// </summary>
    public static IReadOnlyList<string> MissingPackages(string projectFilePath, string assetsPath)
    {
        var declared = DeclaredPackages(projectFilePath);
        if (declared.Count == 0 || RestoredDependencies(assetsPath) is not { } restored)
        {
            return [];
        }

        return declared.Where(name => !restored.Contains(name)).Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// References the SDK removes or supplies itself (a shared framework referenced as a package,
    /// NETSDK1080), which a correct restore never lists as a dependency.
    /// </summary>
    private static readonly HashSet<string> SdkImplicitPackages = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.AspNetCore.App", "Microsoft.AspNetCore.All", "Microsoft.NETCore.App",
        "Microsoft.WindowsDesktop.App", "NETStandard.Library",
    };

    private static HashSet<string> DeclaredPackages(string projectFilePath)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var document = XDocument.Load(projectFilePath);
            foreach (var item in document.Descendants().Where(e => e.Name.LocalName == "PackageReference"))
            {
                // A Condition anywhere above it, a <Choose> branch, or a <Target> body: whether the
                // item exists depends on evaluation this check doesn't do — skipped, never guessed.
                if (item.AncestorsAndSelf().Any(e => e.Attribute("Condition") is not null
                        || e.Name.LocalName is "Choose" or "When" or "Otherwise" or "Target")
                    || item.Attribute("Include")?.Value is not { Length: > 0 } include)
                {
                    continue;
                }

                foreach (string name in include.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (name.IndexOfAny(['$', '@', '%', '*', '?']) < 0 && !SdkImplicitPackages.Contains(name))
                    {
                        names.Add(name);
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or XmlException)
        {
            names.Clear();
        }

        return names;
    }

    /// <summary>
    /// The package names under <c>project.frameworks.*.dependencies</c> of an assets file, or null
    /// when it can't be read. Streams past every other section: assets files run to megabytes.
    /// </summary>
    private static HashSet<string>? RestoredDependencies(string assetsPath)
    {
        try
        {
            var reader = new Utf8JsonReader(File.ReadAllBytes(assetsPath));
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return null;
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                bool isProject = reader.ValueTextEquals("project"u8);
                reader.Read();
                if (!isProject)
                {
                    reader.Skip();
                    continue;
                }

                using var project = JsonDocument.ParseValue(ref reader);
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (project.RootElement.TryGetProperty("frameworks", out var frameworks) && frameworks.ValueKind == JsonValueKind.Object)
                {
                    foreach (var framework in frameworks.EnumerateObject())
                    {
                        if (framework.Value.TryGetProperty("dependencies", out var dependencies) && dependencies.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var dependency in dependencies.EnumerateObject())
                            {
                                names.Add(dependency.Name);
                            }
                        }
                    }
                }

                return names;
            }

            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
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
