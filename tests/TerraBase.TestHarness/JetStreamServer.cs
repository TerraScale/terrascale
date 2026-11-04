using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace TerraBase.Testing;

internal sealed partial class JetStreamServer : IAsyncDisposable
{
    private readonly Process _process;
    private readonly string _directory;

    private JetStreamServer(Process process, string directory, Uri url, string token)
    {
        _process = process;
        _directory = directory;
        Url = url;
        Token = token;
    }

    public Uri Url { get; }

    public string Token { get; }

    public static async Task<JetStreamServer> StartAsync(CancellationToken cancellationToken)
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-jetstream-").FullName;
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var config = Path.Combine(directory, "nats.conf");
        await File.WriteAllTextAsync(config, $$"""
            listen: "127.0.0.1:-1"
            max_payload: 8MB
            authorization { token: "{{token}}" }
            jetstream { store_dir: "{{Path.Combine(directory, "store")}}" }
            """, cancellationToken);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(config, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        var process = new Process
        {
            StartInfo = new ProcessStartInfo("nats-server", ["-c", config]) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false },
        };
        if (!process.Start())
        {
            throw new InvalidOperationException("nats-server did not start; it must be on PATH");
        }

        var listening = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.ErrorDataReceived += (_, line) =>
        {
            if (line.Data is { } text && Listening().Match(text) is { Success: true } match)
            {
                listening.TrySetResult(new Uri("nats://127.0.0.1:" + match.Groups[1].Value));
            }
        };
        process.Exited += (_, _) => listening.TrySetException(new InvalidOperationException("nats-server exited before listening"));
        process.EnableRaisingEvents = true;
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();
        try
        {
            var url = await listening.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            return new JetStreamServer(process, directory, url, token);
        }
        catch
        {
            process.Kill(entireProcessTree: true);
            process.Dispose();
            Directory.Delete(directory, recursive: true);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }

        _process.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    [GeneratedRegex(@"Listening for client connections on [^\s]*:(\d+)")]
    private static partial Regex Listening();
}
