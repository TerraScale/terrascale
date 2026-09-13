// <copyright file="EngineSupervisor.Process.cs" company="TerraScale">
// TerraBase (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api;

using System.Globalization;
using System.Diagnostics;
using System.Net.Sockets;
using TerraBase.Protocol;

public sealed partial class EngineSupervisor
{
    private async Task EnsureEngineAsync(CancellationToken cancellationToken)
    {
        if (this.Options.ManagedBinary is null)
        {
            // Attached mode: the engine socket must exist; the supervisor
            // neither spawns nor restarts it, and reports truthfully.
            await WaitForSocketAsync(this.Options.SocketPath, this.Options.StartTimeout, cancellationToken).ConfigureAwait(false);
            return;
        }

        var current = Volatile.Read(ref this.engineProcess);
        if (current is { HasExited: false })
        {
            return;
        }

        var instance = checked(this.Generation + 1);
        var start = new ProcessStartInfo
        {
            FileName = this.Options.ManagedBinary,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(this.Options.SocketPath);
        start.ArgumentList.Add(instance.ToString(CultureInfo.InvariantCulture));
        var process = Process.Start(start) ?? throw new EngineUnavailableException("the engine process did not start");
        Volatile.Write(ref this.engineProcess, process);
        // The pipes drain in the background so a chatty engine can never
        // block on a full pipe; the supervisor reads health through TBEP.
        _ = process.StandardOutput.ReadToEndAsync(this.stopped.Token);
        _ = process.StandardError.ReadToEndAsync(this.stopped.Token);
        // Engine-process loss is detected the moment it happens — the
        // truthful loss handling (typed refusals, closed streams, ended
        // sessions) runs before any caller has to discover it.
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => _ = this.LoseEngineAsync("the engine process exited");

        await WaitForSocketAsync(this.Options.SocketPath, this.Options.StartTimeout, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WaitForSocketAsync(string path, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                // The readiness probe rides the sanctioned client (the
                // SE-14 TBEP path) — the supervisor owns no raw socket:
                // the engine serves when it accepts a client connection.
                await using var probe = TbepChannel.Connect(path);
                return;
            }
            catch (Exception failure) when (failure is SocketException or ObjectDisposedException)
            {
                // The engine socket does not serve yet; keep waiting
                // within the bound.
                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken).ConfigureAwait(false);
            }
        }

        throw new EngineUnavailableException($"the engine socket {path} did not serve within the start bound");
    }
}
