using System.Diagnostics;
using System.Text;
using TerraScale.Dashboard.E2E.Demo;

namespace TerraScale.Dashboard.E2E
{
    // A cold synthetic host launched on port 0. The child announces the origin the OS bound, so no test reserves a port
    // ahead of launch. Both pipes are drained for the child's whole life, so a chatty child can never block on output.
    internal sealed class FixtureProcess : IAsyncDisposable
    {
        private static readonly TimeSpan StartupAllowance = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan CleanupAllowance = TimeSpan.FromSeconds(10);

        private readonly Process process;
        private readonly TaskCompletionSource<int> announced = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly StringBuilder output = new();
        private readonly StringBuilder error = new();
        private readonly Task reading;

        private FixtureProcess(Process process)
        {
            this.process = process;
            reading = Task.WhenAll(ReadOutputAsync(), ReadErrorAsync());
        }

        public Process Process => process;

        public bool Announced => announced.Task.IsCompletedSuccessfully;

        public int AnnouncedPort => announced.Task.IsCompletedSuccessfully ? announced.Task.Result : 0;

        public static FixtureProcess Start(ProcessStartInfo start)
        {
            return new FixtureProcess(Process.Start(start) ?? throw new IOException("Could not launch the synthetic fixture."));
        }

        // Waits for the child's origin announcement; fails as soon as the child closes stdout, and never waits longer than the startup allowance.
        public async Task<Uri> WaitForOriginAsync()
        {
            var port = await announced.Task.WaitAsync(StartupAllowance);
            return new Uri($"http://127.0.0.1:{port}/", UriKind.Absolute);
        }

        // Kills the child tree and returns its drained stdout followed by stderr. Safe to call more than once.
        public async Task<string> StopAsync()
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) { } // The child exited between the check and the kill.
            }

            await process.WaitForExitAsync().WaitAsync(CleanupAllowance);
            await reading.WaitAsync(CleanupAllowance);
            lock (output)
            {
                lock (error)
                {
                    return output.ToString() + error.ToString();
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            try { await StopAsync(); }
            finally { process.Dispose(); }
        }

        private async Task ReadOutputAsync()
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync()) is not null)
            {
                lock (output)
                {
                    output.AppendLine(line);
                }

                if (line.StartsWith(PublicDemo.OriginPrefix, StringComparison.Ordinal) &&
                    Uri.TryCreate(line[PublicDemo.OriginPrefix.Length..], UriKind.Absolute, out var address))
                {
                    announced.TrySetResult(address.Port);
                }
            }

            announced.TrySetException(new IOException("The synthetic fixture closed its output before announcing its origin."));
        }

        private async Task ReadErrorAsync()
        {
            var text = await process.StandardError.ReadToEndAsync();
            lock (error)
            {
                error.Append(text);
            }
        }
    }
}
