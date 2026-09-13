// <copyright file="Program.cs" company="TerraScale">
// TerraBase (SE-13). See LICENSE.md.
// </copyright>

namespace TerraBase.Cli;

using System.Text;

/// <summary>
/// The production entry point of the TerraBase operator CLI (SE-13): a
/// local tool without an auth gate (PD-2026-08-26-11) that drives the
/// API host's typed surface by default, or a local engine process
/// directly through the SE-14 TBEP client in its documented direct
/// mode. The operator's interrupt stops a following tail gracefully.
/// </summary>
public static class Program
{
    /// <summary>Runs the CLI.</summary>
    /// <param name="args">The command-line arguments.</param>
    /// <returns>The exit code.</returns>
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        using var source = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            source.Cancel();
        };

        return await Cli.RunAsync(args, Console.Out, Console.Error, source.Token).ConfigureAwait(false);
    }
}
