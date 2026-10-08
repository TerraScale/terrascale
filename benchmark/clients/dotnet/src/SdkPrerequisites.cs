using TerraScale.DotNet.TerraBase;

namespace BenchClient;

internal static class SdkPrerequisites
{
    internal static string Revision { get; } = ReadRevision();
    internal static string Version { get; } = typeof(TerraBaseDocumentsClient).Assembly.GetName().Version!.ToString();

    internal static PreflightException BlockedRun() => new("SDK_BLOCKED",
        $"ts-dotnet {Version} ({Revision}) lacks public project-JWT database create/delete and index APIs. " +
        "Land the SDK prerequisites before running; no credentials were read, no requests sent, " +
        "no database created, and no results opened.");

    private static string ReadRevision()
    {
        var revision = typeof(SdkPrerequisites).Assembly.GetManifestResourceStream("BenchClient.sdk.rev")!;
        using var reader = new StreamReader(revision);
        return reader.ReadToEnd().Trim();
    }
}
