using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Http.HttpResults;
using RazorSlices;
using TerraScale.Dashboard.Platform.Datastar;

namespace TerraScale.Dashboard.Common.Live;

public sealed record LiveFragment(RazorSlice Slice, bool Complete = false);

public sealed class LiveView(TimeProvider time, IHostApplicationLifetime applicationLifetime, LiveAdmission admission)
{
    internal static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly AsyncLocal<bool> streaming = new();

    public static bool Streaming => streaming.Value;

    public static HtmlString Initializer(AccountContext context, string path)
    {
        var expression = "@get('" + JavaScriptEncoder.Default.Encode(WorkspaceUrl.For(context, path)) + "', {retry: 'always', requestCancellation: 'cleanup'})";
        return new("data-init=\"" + HtmlEncoder.Default.Encode(expression) + "\"");
    }

    public async Task<IResult> StreamAsync(HttpContext http, Func<CancellationToken, Task<LiveFragment>> read,
        IAsyncEnumerable<DateTimeOffset> changes, CancellationToken cancellationToken)
    {
        var admitted = admission.Enter(http);
        return admitted switch
        {
            LiveLease lease => await StreamAsync(http, lease, read, changes, cancellationToken),
            LiveRefused refused => Refuse(http, refused),
        };
    }

    private static StatusCodeHttpResult Refuse(HttpContext http, LiveRefused refused)
    {
        http.Response.Headers.RetryAfter = "5";
        return TypedResults.StatusCode(refused.Status);
    }

    private async Task<IResult> StreamAsync(HttpContext http, LiveLease lease, Func<CancellationToken, Task<LiveFragment>> read,
        IAsyncEnumerable<DateTimeOffset> changes, CancellationToken cancellationToken)
    {
        using var held = lease;
        streaming.Value = true;
        var aborted = http.RequestAborted;
        using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(15), time);
        var stopping = applicationLifetime.ApplicationStopping;
        using var stream = CancellationTokenSource.CreateLinkedTokenSource(aborted, cancellationToken, lifetime.Token, stopping);
        http.RequestAborted = stream.Token;
        try
        {
            http.Response.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers["X-Accel-Buffering"] = "no";
            await DatastarEvents.KeepAliveAsync(http, stream.Token);
            string? previous = null;
            var lastWrite = time.GetTimestamp();
            await foreach (var _ in changes.WithCancellation(stream.Token))
            {
                var reading = read(stream.Token);
                using (var pause = CancellationTokenSource.CreateLinkedTokenSource(stream.Token))
                {
                    while (await Task.WhenAny(reading, Task.Delay(KeepAliveInterval, time, pause.Token)) != reading && !stream.IsCancellationRequested)
                    {
                        await DatastarEvents.KeepAliveAsync(http, stream.Token);
                        lastWrite = time.GetTimestamp();
                    }

                    await pause.CancelAsync();
                }

                var fragment = await reading;
                fragment.Slice.HttpContext = http;
                fragment.Slice.ServiceProvider = http.RequestServices;
                var html = await fragment.Slice.RenderAsync(cancellationToken: stream.Token);
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(html)));
                if (hash != previous)
                {
                    await DatastarEvents.PatchElementsAsync(http, html, stream.Token);
                    previous = hash;
                    lastWrite = time.GetTimestamp();
                }
                else if (time.GetElapsedTime(lastWrite) >= KeepAliveInterval)
                {
                    await DatastarEvents.KeepAliveAsync(http, stream.Token);
                    lastWrite = time.GetTimestamp();
                }

                if (fragment.Complete)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when ((lifetime.IsCancellationRequested || stopping.IsCancellationRequested) && !aborted.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return TypedResults.Empty;
        }
        finally
        {
            http.RequestAborted = aborted;
        }

        return TypedResults.Empty;
    }

    public async IAsyncEnumerable<DateTimeOffset> ChangesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return time.GetUtcNow();
        using var timer = new PeriodicTimer(PollInterval, time);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            yield return time.GetUtcNow();
        }
    }
}
