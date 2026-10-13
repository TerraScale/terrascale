using System.Diagnostics;

namespace TerraBase.E2E;

/// <summary>Runs flyctl for a pinned disposable app's run: its read-only target checks and its machine stops and starts.</summary>
internal static class Flyctl
{
    public static async Task<string> RunAsync(params string[] arguments)
    {
        var start = new ProcessStartInfo("flyctl") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("flyctl did not start");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"flyctl {string.Join(' ', arguments)} exited {process.ExitCode}: {await error}");
        }

        return await output;
    }
}
