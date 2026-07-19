namespace Slnmap.Analysis;

/// <summary>
/// Recognizes the "required .NET SDK is not installed" failure that the out-of-process MSBuild build
/// host raises while opening a solution, and turns it into a short, actionable message.
/// </summary>
public static class SdkResolutionDiagnostics
{
    private static readonly string[] Signatures =
    [
        "hostfxr_resolve_sdk2",
        "compatible .NET SDK was not found",
        "requested SDK version",
    ];

    /// <summary>True when <paramref name="exception"/> (or any inner exception) looks like an SDK-resolution failure.</summary>
    public static bool IsSdkResolutionFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            foreach (string signature in Signatures)
            {
                if (current.Message.Contains(signature, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Builds the two-line message shown to the user (required vs installed, pointing at global.json and doctor).</summary>
    public static string BuildMessage(string solutionPath, SdkRequirement? requirement, IReadOnlyList<string> installedVersions)
    {
        string installed = installedVersions.Count > 0 ? string.Join(", ", installedVersions) : "none";
        string line1 = requirement is not null
            ? $"A required .NET SDK is not installed: '{requirement.GlobalJsonPath}' pins SDK {requirement.Version} (rollForward: {requirement.RollForward}), but installed SDKs are: {installed}."
            : $"A required .NET SDK to load '{solutionPath}' is not installed. Installed SDKs: {installed}.";
        const string line2 = "Install the required SDK from https://dotnet.microsoft.com/download (or adjust global.json), then run 'slnmap doctor' to re-check.";
        return line1 + Environment.NewLine + line2;
    }
}
