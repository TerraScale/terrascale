using TerraScale.DotNet.TerraBase;

namespace BenchClient;

internal static class SdkPrerequisites
{
    internal static string Revision => ReadRevision();
    internal static string Version => typeof(TerraBaseDocumentsClient).Assembly.GetName().Version?.ToString()
        ?? throw new PreflightException("PREFLIGHT_FAILED", "SDK assembly version is missing; rebuild the client.");

    internal static PreflightException BlockedRun() => new("SDK_BLOCKED",
        $"The workload preflight has not implemented its required SDK lifecycle and measurement contract for ts-dotnet {Version} ({Revision}). " +
        "Complete that benchmark implementation before running; no credentials were read, no requests sent, " +
        "no database created, and no results opened.");

    private static string ReadRevision()
    {
        using var revision = typeof(SdkPrerequisites).Assembly.GetManifestResourceStream("BenchClient.sdk.rev")
            ?? throw new PreflightException("PREFLIGHT_FAILED", "Embedded SDK revision is missing; rebuild the client.");
        using var reader = new StreamReader(revision);
        return reader.ReadToEnd().Trim();
    }
}
