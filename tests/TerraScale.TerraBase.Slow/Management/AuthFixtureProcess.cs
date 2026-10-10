using System.Diagnostics;

namespace TerraBase.Slow.Management;

// Children inherit the suite's locally applied nice priority; CI runs at its normal priority.
internal sealed class AuthFixtureProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task _stdout;
    private readonly Task _stderr;
    private readonly TaskCompletionSource<Uri> _http = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<Uri> _https = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _signingRing = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private AuthFixtureProcess(Process process)
    {
        _process = process;
        _stdout = ReadAsync(process.StandardOutput);
        _stderr = ReadAsync(process.StandardError);
    }

    public static AuthFixtureProcess Start(string executable, string directory, IEnumerable<string> arguments, Dictionary<string, string>? environment = null)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // Download credentials are passed explicitly only to acquisition, never inherited by services.
        foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("TS_AUTH_", StringComparison.Ordinal)
            || name.StartsWith("TERRABASE_", StringComparison.Ordinal)
            || name.StartsWith("FORGEJO_", StringComparison.Ordinal)
            || name.StartsWith("GITHUB_", StringComparison.Ordinal)
            || name.StartsWith("ACTIONS_", StringComparison.Ordinal)
            || name is "ORG_READ_TOKEN" or "TS_RELEASE_OCI_KEY" or "SSH_AUTH_SOCK" or "GIT_ASKPASS").ToArray())
        {
            start.Environment.Remove(name);
        }

        start.Environment["DOTNET_ENVIRONMENT"] = "Development";
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment["Logging__LogLevel__Default"] = "Information";
        start.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = Path.Combine(directory, "bundle");
        foreach (var (name, value) in environment ?? [])
        {
            start.Environment[name] = value;
        }

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        return new AuthFixtureProcess(Process.Start(start) ?? throw new InvalidOperationException("the fixture process did not start"));
    }

    public Task<Uri> HttpAsync(CancellationToken token) => WaitAsync(_http.Task, token);
    public Task<Uri> HttpsAsync(CancellationToken token) => WaitAsync(_https.Task, token);
    public Task SigningRingAsync(CancellationToken token) => WaitAsync(_signingRing.Task, token);

    public async Task CompleteAsync(CancellationToken token)
    {
        await _process.WaitForExitAsync(token);
        await Task.WhenAll(_stdout, _stderr).WaitAsync(token);
        if (_process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{Path.GetFileName(_process.StartInfo.FileName)} exited with code {_process.ExitCode}");
        }
    }

    private async Task<T> WaitAsync<T>(Task<T> ready, CancellationToken token)
    {
        await WaitAsync((Task)ready, token);
        return await ready;
    }

    private async Task WaitAsync(Task ready, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        var exited = _process.WaitForExitAsync(deadline.Token);
        try
        {
            if (await Task.WhenAny(ready, exited).WaitAsync(deadline.Token) == exited)
            {
                await exited;
                throw new InvalidOperationException($"{Path.GetFileName(_process.StartInfo.FileName)} exited before fixture readiness with code {_process.ExitCode}");
            }

            await ready;
        }
        finally
        {
            // End the losing process-exit wait too; no readiness waiter outlives its deadline.
            await deadline.CancelAsync();
        }
    }

    private async Task ReadAsync(StreamReader output)
    {
        // Retain only startup signals: process output can contain credentials or token material.
        while (await output.ReadLineAsync() is { } line)
        {
            const string listening = "Now listening on: ";
            var text = line.Trim();
            if (text.StartsWith(listening, StringComparison.Ordinal)
                && Uri.TryCreate(text[listening.Length..], UriKind.Absolute, out var address) && address.Port > 0)
            {
                (address.Scheme == Uri.UriSchemeHttps ? _https : _http).TrySetResult(new UriBuilder(address) { Host = "localhost" }.Uri);
            }

            if (text == "Wrote a fresh signing key ring; tokens signed by any replaced key no longer validate")
            {
                _signingRing.TrySetResult();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException) when (_process.HasExited)
            {
                // A child can exit between the check and termination.
            }

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _process.WaitForExitAsync(deadline.Token);
            await Task.WhenAll(_stdout, _stderr).WaitAsync(deadline.Token);
        }
        finally
        {
            _process.Dispose();
        }
    }
}
