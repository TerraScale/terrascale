using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using TUnit.Core;
using Win32Exception = System.ComponentModel.Win32Exception;

namespace TerraScale.Dashboard.E2E.Platform;

// Explicit positive qualification of the real production startup and state
// readiness only. No demo graph, descriptor injection or transport overrides.
// Missing automatic delivery or a serving producer fails this journey.
[Explicit]
[Category("LiveProduction")]
public sealed class LiveProductionReadinessJourneyTests
{
    [Test]
    public async Task AutomaticallyDeliveredIdentityAndServingStateBecomeReadyThroughTheProductionProgram()
    {
        var root = FindRoot();
        var project = Path.Combine(root, "src", "TerraScale.Dashboard");
        var application = Path.Combine(project, "bin", "Release", "net11.0", "TerraScale.Dashboard.dll");
        await Assert.That(File.Exists(application)).IsTrue()
            .Because("This live journey executes the actual previously built Release production program.");
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = project,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { application, "--environment", "Production", "--urls", "http://127.0.0.1:0", "--contentRoot", project })
            start.ArgumentList.Add(argument);

        Process? process = null;
        Task output = Task.CompletedTask;
        Task errors = Task.CompletedTask;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            process = Process.Start(start);
            await Assert.That(process is not null).IsTrue()
                .Because("The actual production program must start without a replacement host factory.");
            var listening = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
            output = DiscardAsync(process!.StandardOutput, listening);
            errors = DiscardAsync(process.StandardError);
            var exited = process.WaitForExitAsync();
            var completed = await Task.WhenAny(listening.Task, exited, Task.Delay(Timeout.InfiniteTimeSpan, deadline.Token));
            await Assert.That(completed == listening.Task && !process.HasExited).IsTrue()
                .Because("Live production startup must complete using automatic machine identity delivery, genuine discovery and the owning persistent state producer. Raw startup logs are deliberately withheld.");
            var origin = await listening.Task;
            using var client = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = TestHttps.Validate, AllowAutoRedirect = false, UseCookies = false })
            { BaseAddress = origin, Timeout = TimeSpan.FromSeconds(10) };
            using var readinessDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            readinessDeadline.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await client.GetAsync("/readyz", HttpCompletionOption.ResponseHeadersRead, readinessDeadline.Token);
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK)
                .Because("The actual production HTTP readiness endpoint must reread every fixed state collection through the official SDK.");
            // The public readiness body names only status, region and dependency states; child logs never reach TRX.
            using var receipt = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(readinessDeadline.Token), cancellationToken: readinessDeadline.Token);
            await Assert.That(receipt.RootElement.GetProperty("status").GetString()).IsEqualTo("ok")
                .Because("Successful production readiness returns its public ok receipt.");
            await Assert.That(!process.HasExited).IsTrue()
                .Because("The genuine production host remains running after its persistent-state readiness reread.");
        }
        catch (Exception failure) when (failure is IOException or HttpRequestException or OperationCanceledException or Win32Exception or UnauthorizedAccessException)
        {
            Assert.Fail("Live production startup or HTTP readiness could not complete within its fixed deadline (" + failure.GetType().Name + "). Dependency payloads and child logs are withheld.");
        }
        finally
        {
            if (process is not null)
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await process.WaitForExitAsync(cleanup.Token);
                    await Task.WhenAll(output, errors).WaitAsync(cleanup.Token);
                }
                catch (Exception failure) when (failure is IOException or OperationCanceledException or Win32Exception or InvalidOperationException)
                {
                    Assert.Fail("The production qualification child and its output drains must finish bounded cleanup (" + failure.GetType().Name + ").");
                }
                finally { process.Dispose(); }
            }
        }
    }

    private static async Task DiscardAsync(StreamReader reader, TaskCompletionSource<Uri>? listening = null)
    {
        var buffer = new char[2048];
        var line = new StringBuilder(256);
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory());
            if (count == 0) return;
            if (listening is null || listening.Task.IsCompleted) continue;
            for (var index = 0; index < count; index++)
            {
                var character = buffer[index];
                if (character is '\r' or '\n')
                {
                    var text = line.ToString();
                    line.Clear();
                    const string prefix = "Now listening on: ";
                    var offset = text.IndexOf(prefix, StringComparison.Ordinal);
                    if (offset >= 0 && Uri.TryCreate(text[(offset + prefix.Length)..].Trim(), UriKind.Absolute, out var origin) &&
                        origin.Scheme == "http" && origin.Host == "127.0.0.1" && origin.Port is > 0 and <= 65535 &&
                        origin.UserInfo.Length == 0 && origin.AbsolutePath == "/" && origin.Query.Length == 0 && origin.Fragment.Length == 0)
                        listening.TrySetResult(origin);
                }
                else
                {
                    // Discard arbitrary log lines while keeping only enough
                    // suffix to identify the fixed loopback listening message.
                    if (line.Length == 256) line.Remove(0, 128);
                    line.Append(character);
                }
            }
        }
    }

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "TerraScale.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("The live production E2E executable must run from the dashboard checkout.");
    }
}
