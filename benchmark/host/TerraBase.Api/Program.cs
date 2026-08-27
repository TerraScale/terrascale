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
    public static async Task<int> Main(string[] args) =>
        await RunAsync(
            Environment.GetEnvironmentVariable("TERRABASE_TBEP_SOCKET") ?? "/run/terrabase/engine.tbep.sock",
            Environment.GetEnvironmentVariable("TERRABASE_TBEP_ENGINE"),
            Environment.GetEnvironmentVariable("TERRABASE_HOST_URL") ?? "http://127.0.0.1:8420").ConfigureAwait(false);

    /// <summary>
    /// Runs the host over one engine endpoint: starts the supervised
    /// engine, serves until cancelled, and answers the exit code. Every
    /// path — including a failed startup — owns everything it started:
    /// the supervisor (and with it the managed engine process it
    /// spawned) and the application are disposed before the exit code.
    /// </summary>
    /// <param name="socket">The engine's UDS socket path.</param>
    /// <param name="managed">The managed engine binary, or <c>null</c> to attach.</param>
    /// <param name="listen">The loopback URL the HTTP surface binds.</param>
    /// <returns>The exit code.</returns>
    internal static async Task<int> RunAsync(string socket, string? managed, string listen)
    {
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
            return 0;
        }
        finally
        {
            // A failed startup owns everything it started, torn down in
            // ownership order: the application stops serving first, then
            // the supervisor kills and disposes the managed engine process
            // it spawned — nothing outlives the exit code, on success or
            // failure, and shutdown never depends on the hub tolerating a
            // dead engine generation mid-drain.
            await bundle.App.DisposeAsync().ConfigureAwait(false);
            await bundle.Supervisor.DisposeAsync().ConfigureAwait(false);
        }
    }
}
