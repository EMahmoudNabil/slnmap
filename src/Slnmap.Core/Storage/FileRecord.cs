namespace Slnmap.Core.Storage;

/// <summary>An analyzed file and the hash of its content at analysis time.</summary>
public sealed record FileRecord(string Path, string ContentHash);
