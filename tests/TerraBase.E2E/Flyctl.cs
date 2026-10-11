using System.Diagnostics;
using System.Text.Json;

namespace TerraBase.E2E;

/// <summary>The flyctl calls the outage test makes: finding the sjc machine, and stopping and starting it.</summary>
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
