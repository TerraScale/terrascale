using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Xml.Linq;
using TerraBase.Fast.Storage;

namespace TerraBase.Fast.Platform;

internal static class IsolatedTestChild
{
    internal static Task RunAsync(Type testClass, string testName, string childFlag) =>
        RunAsync(childFlag, testClass.Namespace!, testClass.Name, testName);

    internal static async Task RunAsync(string childFlag, string namespaceName, string className, string testName)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("The test executable path is unavailable.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add(Path.GetFullPath(Environment.GetCommandLineArgs()[0]));
        }

        start.ArgumentList.Add("--treenode-filter");
        // The tree root is the assembly name, which is no longer the namespace root (TerraBase.Fast).
        var assembly = typeof(IsolatedTestChild).Assembly.GetName().Name;
        start.ArgumentList.Add($"/{assembly}/{namespaceName}/{className}/{testName}");
        start.ArgumentList.Add("--minimum-expected-tests");
        start.ArgumentList.Add("1");
        start.ArgumentList.Add("--no-progress");
        start.ArgumentList.Add("--disable-logo");
        start.ArgumentList.Add("--exit-on-process-exit");
        start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--report-trx");
        start.ArgumentList.Add("--report-trx-filename");
        start.ArgumentList.Add("isolated.trx");
        start.Environment[childFlag] = "1";
        var results = Directory.CreateTempSubdirectory("terrabase-isolated-test-");
        start.ArgumentList.Add("--results-directory");
        start.ArgumentList.Add(results.FullName);
        start.ArgumentList.Add("--report-html-filename");
        start.ArgumentList.Add(Path.Combine(results.FullName, "isolated.html"));
        start.ArgumentList.Add("--junit-output-path");
        start.ArgumentList.Add(Path.Combine(results.FullName, "isolated-junit.xml"));
        Exception? primaryFailure = null;
        try
        {
            using var child = Process.Start(start) ?? throw new InvalidOperationException("The isolated test could not start.");
            var executionToken = TUnit.Core.TestContext.Current!.Execution.CancellationToken;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(executionToken);
            deadline.CancelAfter(TimeSpan.FromMinutes(2));
            using var outputCancellation = new CancellationTokenSource();
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            var readingOutput = ReadChildOutputAsync(child.StandardOutput, stdout, outputCancellation.Token);
            var readingError = ReadChildOutputAsync(child.StandardError, stderr, outputCancellation.Token);
            // Retain actual exit even when the test's wait or its output readers are cancelled.
            var exited = child.WaitForExitAsync(CancellationToken.None);
            var completed = Task.WhenAll(exited, readingOutput, readingError);
            try
            {
                await completed.WaitAsync(deadline.Token);
                if (child.ExitCode != 0)
                {
                    throw new InvalidOperationException($"Isolated test exited with code {child.ExitCode}.\n{CapturedOutput()}");
                }

                var reportPath = Path.Combine(results.FullName, "isolated.trx");
                // A successful exit with a missing filter or skipped test is not proof the regression ran.
                await Assert.That(new FileInfo(reportPath).Length).IsLessThanOrEqualTo(1024 * 1024);
                await using var report = File.OpenRead(reportPath);
                var document = await XDocument.LoadAsync(report, LoadOptions.None, deadline.Token);
                XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
                var result = document.Descendants(ns + "UnitTestResult").Single();
                await Assert.That((string?)result.Attribute("outcome")).IsEqualTo("Passed");
                await Assert.That((string?)result.Attribute("testName")).IsEqualTo(testName);
                var counters = document.Descendants(ns + "Counters").Single();
                await Assert.That((string?)counters.Attribute("total")).IsEqualTo("1");
                await Assert.That((string?)counters.Attribute("passed")).IsEqualTo("1");
                await Assert.That((string?)counters.Attribute("notExecuted")).IsEqualTo("0");
            }
            catch (OperationCanceledException error) when (deadline.IsCancellationRequested && !executionToken.IsCancellationRequested)
            {
                var failure = new TimeoutException($"The isolated test exceeded two minutes.\n{CapturedOutput()}", error);
                primaryFailure = failure;
                throw failure;
            }
            catch (Exception error)
            {
                if (error is OperationCanceledException)
                {
                    var output = CapturedOutput();
                    error.Data["ChildOutput"] = output;
                    // The runner's timeout report omits exception Data and may already have flushed test output.
                    try
                    {
                        using var standardError = Console.OpenStandardError();
                        using var diagnostics = new StreamWriter(standardError, leaveOpen: true);
                        diagnostics.WriteLine(output);
                    }
                    catch (Exception emissionError)
                    {
                        error.Data["ChildOutputEmissionFailure"] = emissionError;
                    }
                }

                primaryFailure = error;
                throw;
            }
            finally
            {
                await CleanupAsync(child, completed, outputCancellation, primaryFailure);
            }

            string CapturedOutput()
            {
                lock (stdout)
                {
                    lock (stderr)
                    {
                        return $"Standard output:\n{stdout}\nStandard error:\n{stderr}";
                    }
                }
            }
        }
        catch (Exception error)
        {
            primaryFailure = error;
            throw;
        }
        finally
        {
            try
            {
                results.Delete(recursive: true);
            }
            catch (Exception error) when (primaryFailure is not null)
            {
                primaryFailure.Data["ResultsCleanupFailure"] = error;
            }
        }
    }

    internal static async Task CleanupAsync(
        Process child, Task completed, CancellationTokenSource outputCancellation, Exception? primaryFailure, TimeSpan? timeout = null)
    {
        using var deadline = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(30));
        var errors = new List<Exception>();
        try
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException) when (child.HasExited)
        {
        }
        catch (Exception error)
        {
            errors.Add(error);
        }

        try
        {
            // Exit and both pipes share the deadline; a descendant can keep a pipe open after exit.
            await completed.WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (completed.IsCanceled)
        {
        }
        catch (OperationCanceledException error) when (deadline.IsCancellationRequested)
        {
            errors.Add(new TimeoutException("The isolated child cleanup exceeded its deadline.", error));
        }
        catch (Exception error)
        {
            errors.Add(completed.Exception ?? error);
        }
        finally
        {
            try
            {
                outputCancellation.Cancel();
            }
            catch (Exception error)
            {
                errors.Add(error);
            }

            foreach (var reader in new[] { child.StandardOutput, child.StandardError })
            {
                try
                {
                    reader.Dispose();
                }
                catch (Exception error)
                {
                    errors.Add(error);
                }
            }

            // Observe late cancellation/disposal faults without reopening the bounded cleanup wait.
            _ = completed.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        await StoragePoolTests.JoinCleanupAsync(errors.Select(static error => Task.FromException(error)), primaryFailure);
    }

    internal static async Task<string> ReadChildOutputAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        await ReadChildOutputAsync(reader, output, cancellationToken);
        return output.ToString();
    }

    private static async Task ReadChildOutputAsync(StreamReader reader, StringBuilder output, CancellationToken cancellationToken)
    {
        const int MaxCharacters = 64 * 1024;
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) != 0)
        {
            // Keep draining after the capture fills so verbose failures cannot block the child.
            lock (output)
            {
                output.Append(buffer, 0, Math.Min(read, MaxCharacters - output.Length));
            }
        }
    }
}
