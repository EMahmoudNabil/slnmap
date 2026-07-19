using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Slnmap.Analysis;

internal static class FileHasher
{
    /// <summary>
    /// SHA-256 (lowercase hex) of each file's bytes on disk. Unreadable files are omitted,
    /// which also excludes them from analysis for this run.
    /// </summary>
    public static Dictionary<string, string> HashFiles(IEnumerable<string> paths, CancellationToken cancellationToken)
    {
        var hashes = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount,
            CancellationToken = cancellationToken,
        };

        Parallel.ForEach(paths, options, path =>
        {
            try
            {
                using var stream = File.OpenRead(path);
                hashes[path] = Convert.ToHexStringLower(SHA256.HashData(stream));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Skip: a file the workspace lists but we cannot read is treated as absent.
            }
        });

        return new Dictionary<string, string>(hashes, StringComparer.Ordinal);
    }
}
