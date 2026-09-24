using System.Diagnostics;

namespace TerraScale.Dashboard;

/// <summary>
/// Runs the CSS partial watcher inside the application started by dotnet watch.
/// This keeps the developer workflow to one command: the watcher rewrites the
/// single static asset and dotnet watch refreshes it in the browser.
/// </summary>
public sealed class CssWatcher(IWebHostEnvironment environment, ILogger<CssWatcher> logger) : IHostedService
{
    private Process? _node;

    public static bool Enabled(IWebHostEnvironment _) =>
        Environment.GetEnvironmentVariable("DOTNET_WATCH") == "1";

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var repository = Path.GetFullPath(Path.Combine(environment.ContentRootPath, "../.."));
        var script = Path.Combine(repository, "scripts/build-css.mjs");
        var start = new ProcessStartInfo("node")
        {
            WorkingDirectory = repository,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(script);
        start.ArgumentList.Add("--watch");

        try
        {
            _node = Process.Start(start);
        }
        catch (System.ComponentModel.Win32Exception error)
        {
            logger.LogError(error, "CSS watcher could not start; edit the generated stylesheet only after rebuilding.");
            return Task.CompletedTask;
        }

        if (_node is not null)
        {
            _node.OutputDataReceived += (_, line) =>
            {
                if (line.Data is { Length: > 0 } text)
                {
                    logger.LogInformation("{Line}", text);
                }
            };
            _node.ErrorDataReceived += (_, line) =>
            {
                if (line.Data is { Length: > 0 } text)
                {
                    logger.LogError("{Line}", text);
                }
            };
            _node.BeginOutputReadLine();
            _node.BeginErrorReadLine();
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_node is { HasExited: false })
        {
            _node.Kill(entireProcessTree: true);
        }

        _node?.Dispose();
        return Task.CompletedTask;
    }
}
