// <copyright file="Program.cs" company="TerraScale">
// TerraBaseV2 (SE-08). See LICENSE.md.
// </copyright>

namespace TerraBase.Api;

/// <summary>
/// The production entry point of the TerraBase C# API host (SE-08): an
/// ASP.NET Core host bound to loopback that speaks the TerraBase Engine
/// Protocol to the Rust engine process over the required UDS binding
/// (PD-2026-08-26-11 D1) through the SE-14 client — the database's
/// external surface in C# per the workspace stack policy.
/// </summary>
public static class Program
{
    /// <summary>Runs the host.</summary>
    /// <param name="args">The command-line arguments.</param>
    /// <returns>The exit code.</returns>
    public static async Task<int> Main(string[] args)
    {
        var socket = Environment.GetEnvironmentVariable("TERRABASE_TBEP_SOCKET") ?? "/run/terrabase/engine.tbep.sock";
        var managed = Environment.GetEnvironmentVariable("TERRABASE_TBEP_ENGINE");
        var listen = Environment.GetEnvironmentVariable("TERRABASE_HOST_URL") ?? "http://127.0.0.1:8420";
        var bundle = TerraBaseHost.Create(new EngineOptions
        {
            SocketPath = socket,
            ManagedBinary = string.IsNullOrWhiteSpace(managed) ? null : managed,
            ListenUrl = listen,
        });

        using var source = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            source.Cancel();
        };

        try
        {
            await bundle.Supervisor.StartAsync(source.Token).ConfigureAwait(false);
        }
        catch (EngineUnavailableException failure)
        {
            Console.Error.WriteLine($"terrabase-host: the engine endpoint is unavailable: {failure.Message}");
            return 1;
        }

        await bundle.App.RunAsync(source.Token).ConfigureAwait(false);
        await bundle.Supervisor.DisposeAsync().ConfigureAwait(false);
        return 0;
    }
}
