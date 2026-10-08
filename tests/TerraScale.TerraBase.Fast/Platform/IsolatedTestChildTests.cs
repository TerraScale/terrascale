using System.Diagnostics;
using System.Globalization;
using TerraBase.Testing;

namespace TerraBase.Fast.Platform;

public sealed class IsolatedTestChildTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Cleanup_bounds_inherited_pipes_and_preserves_the_original_failure(bool hasPrimaryFailure)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current!.Execution.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        await using var server = await TerraBaseTestServer.StartAsync();
        using var health = await server.Client.GetAsync(new Uri("/healthz", UriKind.Relative), deadline.Token);
        health.EnsureSuccessStatusCode();

        var directory = Directory.CreateTempSubdirectory("terrabase-child-cleanup-");
        using var outputCancellation = new CancellationTokenSource();
        var start = new ProcessStartInfo("bash")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("""
            set -e
            mkfifo "$1"
            "$BASH" -c 'exec 3<> "$1"; printf "%s\n" "$BASHPID"; IFS= read -r command <&3' pipe-holder "$1" &
            IFS= read -r command
            """);
        start.ArgumentList.Add("cleanup-parent");
        start.ArgumentList.Add(Path.Combine(directory.FullName, "release.fifo"));
        Process? child = null;
        Process? descendant = null;
        Task? completed = null;
        Exception? failure = null;
        try
        {
            child = Process.Start(start) ?? throw new InvalidOperationException("The pipe-holder fixture could not start.");
            // Capture the descendant before allowing its parent to exit, so every failure can clean it up.
            var pid = await child.StandardOutput.ReadLineAsync(deadline.Token);
            descendant = Process.GetProcessById(int.Parse(pid!, CultureInfo.InvariantCulture));
            var stdout = MemoryBudgetTests.ReadChildOutputAsync(child.StandardOutput, outputCancellation.Token);
            var stderr = MemoryBudgetTests.ReadChildOutputAsync(child.StandardError, outputCancellation.Token);
            var exited = child.WaitForExitAsync(outputCancellation.Token);
            completed = Task.WhenAll(exited, stdout, stderr);
            await child.StandardInput.WriteLineAsync("exit".AsMemory(), deadline.Token);
            await child.StandardInput.FlushAsync(deadline.Token);
            await exited.WaitAsync(deadline.Token);
            await Assert.That(descendant.HasExited).IsFalse();
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
            await Assert.That(descendant.HasExited).IsFalse();
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
                await Task.WhenAll(KillAndJoinAsync(child), KillAndJoinAsync(descendant), outputCancellation.CancelAsync());
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
                descendant?.Dispose();
                child?.Dispose();
                try
                {
                    directory.Delete(recursive: true);
                }
                catch (Exception error) when (failure is not null)
                {
                    failure.Data["DirectoryCleanupFailure"] = error;
                }
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
