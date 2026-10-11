using System.Diagnostics;
using System.Globalization;
using TerraBase.Testing;

namespace TerraBase.Fast.Platform;

public sealed class IsolatedTestChildTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Cleanup_bounds_open_pipes_and_preserves_the_original_failure(bool hasPrimaryFailure)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current!.Execution.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        await using var server = await TerraBaseTestServer.StartAsync();
        using var health = await server.Client.GetAsync(new Uri("/healthz", UriKind.Relative), deadline.Token);
        health.EnsureSuccessStatusCode();

        using var outputCancellation = new CancellationTokenSource();
        var start = new ProcessStartInfo("bash")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("IFS= read -r command");
        Process? child = null;
        Process? pipeHolder = null;
        Task? completed = null;
        Exception? failure = null;
        try
        {
            child = Process.Start(start) ?? throw new InvalidOperationException("The pipe-holder fixture could not start.");
            // Own both processes directly: container PID 1 may never reap an orphaned descendant.
            var holderStart = new ProcessStartInfo("bash")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
            };
            holderStart.ArgumentList.Add("-c");
            holderStart.ArgumentList.Add("""
                set -e
                exec 3> "$1" 4> "$2"
                printf 'ready\n'
                IFS= read -r command
                """);
            holderStart.ArgumentList.Add("pipe-holder");
            var childPid = child.Id.ToString(CultureInfo.InvariantCulture);
            holderStart.ArgumentList.Add($"/proc/{childPid}/fd/1");
            holderStart.ArgumentList.Add($"/proc/{childPid}/fd/2");
            pipeHolder = Process.Start(holderStart) ?? throw new InvalidOperationException("The pipe holder could not start.");
            await Assert.That(await pipeHolder.StandardOutput.ReadLineAsync(deadline.Token)).IsEqualTo("ready");
            var stdout = IsolatedTestChild.ReadChildOutputAsync(child.StandardOutput, outputCancellation.Token);
            var stderr = IsolatedTestChild.ReadChildOutputAsync(child.StandardError, outputCancellation.Token);
            var exited = child.WaitForExitAsync(outputCancellation.Token);
            completed = Task.WhenAll(exited, stdout, stderr);
            await child.StandardInput.WriteLineAsync("exit".AsMemory(), deadline.Token);
            await child.StandardInput.FlushAsync(deadline.Token);
            await exited.WaitAsync(deadline.Token);
            await Assert.That(pipeHolder.HasExited).IsFalse();
            await Assert.That(stdout.IsCompleted).IsFalse();
            await Assert.That(stderr.IsCompleted).IsFalse();

            var primary = hasPrimaryFailure ? new InvalidOperationException("original isolated-test failure") : null;
            AggregateException cleanupFailure;
            if (primary is not null)
            {
                await IsolatedTestChild.CleanupAsync(child, completed, outputCancellation, primary,
                    TimeSpan.FromMilliseconds(100)).WaitAsync(TimeSpan.FromSeconds(5));
                cleanupFailure = (AggregateException)primary.Data["CleanupFailure"]!;
            }
            else
            {
                cleanupFailure = (await Assert.ThrowsAsync<AggregateException>(() =>
                    IsolatedTestChild.CleanupAsync(child, completed, outputCancellation, null,
                        TimeSpan.FromMilliseconds(100)).WaitAsync(TimeSpan.FromSeconds(5))))!;
            }

            await Assert.That(cleanupFailure.Flatten().InnerExceptions.OfType<TimeoutException>().Any()).IsTrue();
            await Assert.That(outputCancellation.IsCancellationRequested).IsTrue();
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(stdout.IsCompleted && stderr.IsCompleted),
                timeout: TimeSpan.FromSeconds(5));
            await Assert.That(pipeHolder.HasExited).IsFalse();
        }
        catch (Exception error)
        {
            failure = error;
            throw;
        }
        finally
        {
            try
            {
                await Task.WhenAll(KillAndJoinAsync(child), KillAndJoinAsync(pipeHolder), outputCancellation.CancelAsync());
                if (completed is not null)
                {
                    try
                    {
                        await completed.WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    catch (Exception) when (completed.IsCompleted)
                    {
                        // Reader cancellation or stream disposal is expected after the bounded cleanup.
                    }
                }
            }
            catch (Exception error) when (failure is not null)
            {
                failure.Data["FixtureCleanupFailure"] = error;
            }
            finally
            {
                pipeHolder?.Dispose();
                child?.Dispose();
            }
        }
    }

    private static async Task KillAndJoinAsync(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException) when (process.HasExited)
        {
        }

        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }
}
