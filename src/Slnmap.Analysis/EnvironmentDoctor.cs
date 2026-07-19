using Microsoft.CodeAnalysis.MSBuild;

namespace Slnmap.Analysis;

/// <summary>The outcome of a single environment check run by <see cref="EnvironmentDoctor"/>.</summary>
public sealed record DiagnosticCheck(string Name, bool Ok, string Detail, string? Fix = null);

/// <summary>
/// Diagnoses whether the host can run Slnmap: a .NET SDK is present (MSBuildWorkspace shells out to
/// it for design-time builds), the Roslyn MSBuild workspace initializes, and the graph directory is
/// writable. Each failing check carries an actionable fix.
/// </summary>
public static class EnvironmentDoctor
{
    /// <param name="databasePath">The graph database path whose directory is checked for write access.</param>
    /// <param name="targetPath">The analysis target (file or directory) used to locate a governing <c>global.json</c>; defaults to the current directory.</param>
    public static async Task<IReadOnlyList<DiagnosticCheck>> RunAsync(
        string databasePath,
        string? targetPath = null,
        CancellationToken cancellationToken = default)
    {
        var sdks = await DotnetSdks.ListAsync(cancellationToken).ConfigureAwait(false);
        return
        [
            EvaluateSdks(sdks.Found, sdks.ExitCode, sdks.Output),
            CheckGlobalJsonSdk(targetPath ?? Directory.GetCurrentDirectory(), sdks.Versions),
            CheckMsBuildWorkspace(),
            CheckGraphDirectory(databasePath),
        ];
    }

    /// <summary>Verifies that the SDK pinned by a governing <c>global.json</c> (if any) is installed.</summary>
    public static DiagnosticCheck CheckGlobalJsonSdk(string targetPath, IReadOnlyList<string> installedVersions)
    {
        const string name = "global.json SDK";
        var requirement = GlobalJson.FindSdkRequirement(targetPath);
        if (requirement is null)
        {
            return new DiagnosticCheck(name, true, "No global.json SDK pin governs the target directory.");
        }

        if (GlobalJson.IsSatisfied(requirement, installedVersions))
        {
            return new DiagnosticCheck(name, true, $"{requirement.GlobalJsonPath} pins SDK {requirement.Version}; a compatible SDK is installed.");
        }

        string installed = installedVersions.Count > 0 ? string.Join(", ", installedVersions) : "none";
        return new DiagnosticCheck(name, false,
            $"{requirement.GlobalJsonPath} pins SDK {requirement.Version} (rollForward: {requirement.RollForward}), which is not installed. Installed: {installed}.",
            "Install the pinned SDK from https://dotnet.microsoft.com/download, or edit global.json to a version you have.");
    }

    /// <summary>Pure evaluation of a <c>dotnet --list-sdks</c> result, split out so it can be tested without a process.</summary>
    public static DiagnosticCheck EvaluateSdks(bool dotnetFound, int exitCode, string stdout)
    {
        const string name = ".NET SDK";
        if (!dotnetFound)
        {
            return new DiagnosticCheck(name, false, "The 'dotnet' command was not found on PATH.",
                "Install the .NET SDK (9.0 or later) from https://dotnet.microsoft.com/download and ensure 'dotnet' is on PATH.");
        }

        var sdks = stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (exitCode != 0 || sdks.Count == 0)
        {
            return new DiagnosticCheck(name, false, "No .NET SDKs are installed (only a runtime may be present).",
                "Install the .NET SDK (9.0 or later) from https://dotnet.microsoft.com/download.");
        }

        return new DiagnosticCheck(name, true, $"{sdks.Count} SDK(s) installed; newest: {sdks[^1]}");
    }

    private static DiagnosticCheck CheckMsBuildWorkspace()
    {
        const string name = "MSBuild workspace";
        try
        {
            using var workspace = MSBuildWorkspace.Create();
            return new DiagnosticCheck(name, true, "Roslyn MSBuild workspace initialized (design-time builds run in an out-of-process build host).");
        }
        catch (Exception e)
        {
            return new DiagnosticCheck(name, false, $"Failed to initialize MSBuild workspace: {e.Message}",
                "Ensure a compatible .NET SDK is installed; on Windows, install the Visual Studio Build Tools if analysis fails to load projects.");
        }
    }

    /// <summary>Probes that the directory holding <paramref name="databasePath"/> can be created and written.</summary>
    public static DiagnosticCheck CheckGraphDirectory(string databasePath)
    {
        const string name = "Graph directory";
        string fix = $"Choose a writable --db path; the directory for '{databasePath}' must be creatable and writable.";
        try
        {
            string fullPath = Path.GetFullPath(databasePath);
            string directory = Path.GetDirectoryName(fullPath) is { Length: > 0 } d ? d : Directory.GetCurrentDirectory();
            Directory.CreateDirectory(directory);

            string probe = Path.Combine(directory, $".slnmap-doctor-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return new DiagnosticCheck(name, true, $"Writable: {directory}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new DiagnosticCheck(name, false, $"Cannot write the graph directory for '{databasePath}': {e.Message}", fix);
        }
    }
}
