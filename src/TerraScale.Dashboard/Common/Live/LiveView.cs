using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Html;
using RazorSlices;
using TerraScale.Dashboard.Platform.Datastar;

namespace TerraScale.Dashboard.Common.Live;

public sealed record LiveFragment(RazorSlice Slice, bool Complete = false);

public sealed class LiveView(TimeProvider time, IHostApplicationLifetime applicationLifetime)
{
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(15);

    public static HtmlString Initializer(AccountContext context, string path)
    {
        var expression = "@get('" + JavaScriptEncoder.Default.Encode(WorkspaceUrl.For(context, path)) + "', {retry: 'always', requestCancellation: 'cleanup'})";
        return new("data-init=\"" + HtmlEncoder.Default.Encode(expression) + "\"");
    }

    public async Task StreamAsync(HttpContext http, Func<CancellationToken, Task<LiveFragment>> read,
        IAsyncEnumerable<DateTimeOffset> changes, CancellationToken cancellationToken)
    {
        var aborted = http.RequestAborted;
        using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(15), time);
        var stopping = applicationLifetime.ApplicationStopping;
        using var stream = CancellationTokenSource.CreateLinkedTokenSource(aborted, cancellationToken, lifetime.Token, stopping);
        http.RequestAborted = stream.Token;
        try
        {
            string? previous = null;
            var lastWrite = time.GetTimestamp();
            await foreach (var _ in changes.WithCancellation(stream.Token))
            {
                var fragment = await read(stream.Token);
                fragment.Slice.HttpContext = http;
                fragment.Slice.ServiceProvider = http.RequestServices;
                var html = await fragment.Slice.RenderAsync(cancellationToken: stream.Token);
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(html)));
                if (hash != previous)
                {
                    if (!http.Response.HasStarted)
                    {
                        http.Response.ContentType = "text/event-stream";
                        http.Response.Headers.CacheControl = "no-store";
                        http.Response.Headers["X-Accel-Buffering"] = "no";
                    }

                    await DatastarEvents.PatchElementsAsync(http, html, stream.Token);
                    previous = hash;
                    lastWrite = time.GetTimestamp();
                }
                else if (http.Response.HasStarted && time.GetElapsedTime(lastWrite) >= KeepAliveInterval)
                {
                    await DatastarEvents.KeepAliveAsync(http, stream.Token);
                    lastWrite = time.GetTimestamp();
                }

                if (fragment.Complete)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when ((lifetime.IsCancellationRequested || stopping.IsCancellationRequested) && !aborted.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return;
        }
        finally
        {
            http.RequestAborted = aborted;
        }
    }

    public async IAsyncEnumerable<DateTimeOffset> ChangesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return time.GetUtcNow();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2), time);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            yield return time.GetUtcNow();
        }
    }
}
