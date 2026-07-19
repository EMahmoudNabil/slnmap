using System.Text.Json;

namespace Slnmap.Analysis;

/// <summary>An SDK version pinned by a <c>global.json</c>.</summary>
/// <param name="GlobalJsonPath">Absolute path of the <c>global.json</c> that pins the version.</param>
/// <param name="Version">The pinned <c>sdk.version</c>.</param>
/// <param name="RollForward">The <c>sdk.rollForward</c> policy (defaults to <c>latestPatch</c> when unspecified).</param>
public sealed record SdkRequirement(string GlobalJsonPath, string Version, string RollForward);

/// <summary>
/// Locates the <c>global.json</c> that governs a directory and evaluates whether an installed SDK
/// satisfies its version pin under the declared <c>rollForward</c> policy.
/// </summary>
public static class GlobalJson
{
    /// <summary>
    /// Finds the nearest <c>global.json</c> at or above <paramref name="startPath"/> (a file or
    /// directory) and returns its SDK pin, or null when there is none or it pins no version.
    /// </summary>
    public static SdkRequirement? FindSdkRequirement(string startPath)
    {
        string full = Path.GetFullPath(startPath);
        string? directory = Directory.Exists(full) ? full : Path.GetDirectoryName(full);

        while (directory is not null)
        {
            string candidate = Path.Combine(directory, "global.json");
            if (File.Exists(candidate) && TryParse(candidate, out var requirement))
            {
                return requirement;
            }

            // The nearest global.json wins; if it exists but pins no SDK version, there is no pin.
            if (File.Exists(candidate))
            {
                return null;
            }

            directory = Directory.GetParent(directory)?.FullName;
        }

        return null;
    }

    /// <summary>True when at least one of <paramref name="installedVersions"/> satisfies <paramref name="requirement"/>.</summary>
    public static bool IsSatisfied(SdkRequirement requirement, IEnumerable<string> installedVersions)
    {
        if (!SdkVersion.TryParse(requirement.Version, out var pinned))
        {
            return true; // Unparseable pin: don't cry wolf.
        }

        foreach (string installedText in installedVersions)
        {
            if (SdkVersion.TryParse(installedText, out var installed) && Allows(pinned, installed, requirement.RollForward))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryParse(string globalJsonPath, out SdkRequirement requirement)
    {
        requirement = null!;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(globalJsonPath));
            if (!document.RootElement.TryGetProperty("sdk", out var sdk)
                || !sdk.TryGetProperty("version", out var version)
                || version.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            string rollForward = sdk.TryGetProperty("rollForward", out var rf) && rf.ValueKind == JsonValueKind.String
                ? rf.GetString()!
                : "latestPatch";
            requirement = new SdkRequirement(globalJsonPath, version.GetString()!, rollForward);
            return true;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // Whether `installed` satisfies `pinned` under the roll-forward policy. Roll-forward only moves
    // UP; a lower major/minor/feature-band never satisfies a higher pin. Because a policy selects the
    // latest patch within an allowed feature band, the pinned patch only constrains the `patch` policy.
    private static bool Allows(SdkVersion pinned, SdkVersion installed, string rollForward) =>
        rollForward.ToLowerInvariant() switch
        {
            "disable" => installed.Equals(pinned),
            // patch rolls forward only within the pinned feature band (needs an equal-or-higher patch).
            "patch" => installed.SameBand(pinned) && installed.Patch >= pinned.Patch,
            // feature: any patch in the pinned band, or a higher band within the same major.minor.
            "feature" or "latestfeature" =>
                installed.Major == pinned.Major && installed.Minor == pinned.Minor
                && installed.FeatureBand >= pinned.FeatureBand,
            // minor: same major, at or above the pinned (minor, feature band).
            "minor" or "latestminor" => installed.Major == pinned.Major && installed.CompareBand(pinned) >= 0,
            // major: at or above the pinned (major, minor, feature band).
            "major" or "latestmajor" => installed.CompareBand(pinned) >= 0,
            // default / latestPatch: the latest patch within the pinned feature band (any patch there).
            _ => installed.SameBand(pinned),
        };

    private readonly record struct SdkVersion(int Major, int Minor, int Patch)
    {
        public int FeatureBand => Patch / 100;

        public bool SameBand(SdkVersion other) =>
            Major == other.Major && Minor == other.Minor && FeatureBand == other.FeatureBand;

        /// <summary>Orders by (major, minor, feature band), ignoring patch-within-band.</summary>
        public int CompareBand(SdkVersion other)
        {
            if (Major != other.Major)
            {
                return Major.CompareTo(other.Major);
            }

            if (Minor != other.Minor)
            {
                return Minor.CompareTo(other.Minor);
            }

            return FeatureBand.CompareTo(other.FeatureBand);
        }

        public static bool TryParse(string? text, out SdkVersion version)
        {
            version = default;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            // Drop any prerelease/build suffix (e.g. "9.0.100-preview").
            string core = text.Split('-', '+')[0];
            string[] parts = core.Split('.');
            if (parts.Length < 3
                || !int.TryParse(parts[0], out int major)
                || !int.TryParse(parts[1], out int minor)
                || !int.TryParse(parts[2], out int patch))
            {
                return false;
            }

            version = new SdkVersion(major, minor, patch);
            return true;
        }
    }
}
