namespace TerraBase.Api;

/// <summary>The entry point for the loopback observability host.</summary>
public static class Program
{
    /// <summary>Runs the host until shutdown.</summary>
    /// <param name="args">The command-line arguments.</param>
    public static async Task Main(string[] args)
    {
        await using var app = TerraBaseHost.Create(
            Environment.GetEnvironmentVariable("TERRABASE_HOST_URL") ?? "http://127.0.0.1:8420");
        await app.RunAsync().ConfigureAwait(false);
    }
}
