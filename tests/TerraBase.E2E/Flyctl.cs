using System.Text.Json;
using System.Diagnostics;

namespace TerraBase.E2E;

/// <summary>Runs flyctl for a pinned disposable app's run: its read-only target checks and its machine stops and starts.</summary>
internal static class Flyctl
{
    // The id of the app's only machine in region.
    public static async Task<string> MachineInRegionAsync(string app, string region)
    {
        using var listed = JsonDocument.Parse(await RunAsync("machine", "list", "--app", app, "--json"));
        var ids = listed.RootElement.EnumerateArray()
            .Where(machine => machine.GetProperty("region").GetString() == region)
            .Select(machine => machine.GetProperty("id").GetString()!)
            .ToArray();
        return ids.Length == 1 ? ids[0] : throw new InvalidOperationException($"{app} must have exactly one machine in {region}, found {ids.Length}");
    }

    /// <summary>
    /// Starts a machine that <c>machine stop</c> took down. Fly refuses a start while the machine is still stopping, so this
    /// first waits, at most two minutes, for it to reach <c>stopped</c>. <paramref name="run"/> replaces flyctl in tests.
    /// </summary>
    public static async Task StartStoppedAsync(string app, string machine, Func<string[], Task<string>>? run = null)
    {
        run ??= RunAsync;
        _ = await run(["machine", "wait", machine, "--app", app, "--state", "stopped", "--wait-timeout", "2m"]);
        _ = await run(["machine", "start", machine, "--app", app]);
    }

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
