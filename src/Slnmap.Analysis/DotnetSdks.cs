using System.Diagnostics;

namespace Slnmap.Analysis;

/// <summary>Queries the installed .NET SDKs via <c>dotnet --list-sdks</c>.</summary>
public static class DotnetSdks
{
    /// <summary>Raw result of <c>dotnet --list-sdks</c> plus the parsed version list.</summary>
    public sealed record Result(bool Found, int ExitCode, string Output, IReadOnlyList<string> Versions);

    public static async Task<Result> ListAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var startInfo = new ProcessStartInfo("dotnet", "--list-sdks")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new Result(false, -1, string.Empty, []);
            }

            string output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return new Result(true, process.ExitCode, output, ParseVersions(output));
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            return new Result(false, -1, string.Empty, []);
        }
    }

    /// <summary>Extracts the version token from each <c>dotnet --list-sdks</c> line (<c>"9.0.314 [C:\path]"</c>).</summary>
    public static IReadOnlyList<string> ParseVersions(string listSdksOutput)
    {
        var versions = new List<string>();
        foreach (string line in listSdksOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int space = line.IndexOf(' ', StringComparison.Ordinal);
            string version = space > 0 ? line[..space] : line;
            if (version.Length > 0)
            {
                versions.Add(version);
            }
        }

        return versions;
    }
}
