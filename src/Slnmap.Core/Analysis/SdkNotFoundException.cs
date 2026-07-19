namespace Slnmap.Core.Analysis;

/// <summary>
/// Thrown when analysis cannot proceed because the .NET SDK required to load the solution is not
/// installed (typically a <c>global.json</c> pinning an SDK version that is absent). Carries a
/// ready-to-print, actionable message; callers should show <see cref="Exception.Message"/> and exit
/// without a stack trace.
/// </summary>
public sealed class SdkNotFoundException : Exception
{
    public SdkNotFoundException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
