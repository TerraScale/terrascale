using System.Data;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Http.HttpResults;
using RazorSlices;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Platform.Datastar;
using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.Common.Live;

public sealed record LiveFragment(RazorSlice Slice, bool Complete = false);
public sealed record LiveTransientFailure;
public sealed record LiveUnauthenticated;
public sealed record LiveMfaRequired;

public readonly union LiveReadOutcome(LiveFragment, LiveTransientFailure, LiveUnauthenticated, LiveMfaRequired);

public sealed class LiveView(TimeProvider time, IHostApplicationLifetime applicationLifetime, LiveAdmission admission)
{
    internal static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxReadBackoff = TimeSpan.FromSeconds(10);
    private static readonly AsyncLocal<bool> streaming = new();

    public static bool Streaming => streaming.Value;

    public static HtmlString Initializer(AccountContext context, string path)
    {
        var url = JavaScriptEncoder.Default.Encode(WorkspaceUrl.For(context, path));
        var expression = $"window.__tsLive?.connect(el, '{url}')";
        return new("data-init=\"" + HtmlEncoder.Default.Encode(expression) + "\"");
    }

    internal static async Task<LiveReadOutcome> ReadWithAccountAsync(HttpContext http, DashboardSessions sessions, AccountService accounts,
        string? errorRegion, Func<AccountContext, CancellationToken, Task<LiveFragment>> read, CancellationToken cancellationToken)
    {
        try
        {
            var required = await AccountRequest.RequireAsync(http, sessions, accounts);
            return required switch
            {
                AccountContext active => await read(active, cancellationToken),
                AccountUnauthenticated => new LiveUnauthenticated(),
                SessionUnavailable => new LiveTransientFailure(),
            };
        }
        catch (Exception error) when (error is OrganizationMfaRequiredException or AccountAccessException { Code: TsAuthGateway.MfaRequiredCode })
        {
            return new LiveMfaRequired();
        }
        catch (AccountAccessException error) when (error.StatusCode == StatusCodes.Status401Unauthorized)
        {
            return new LiveUnauthenticated();
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested &&
            (error is TimeoutException or IOException or JsonException or DBConcurrencyException or OperationCanceledException
                or HttpRequestException { StatusCode: null } ||
                DashboardStorageOutcomes.Find(error) is not null ||
                (DatabaseResults.KnownFailure(error) || error is HttpRequestException) && IsTransientStatus(DatabaseResults.Status(error))))
        {
            return new LiveTransientFailure();
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested && (DatabaseResults.KnownFailure(error) || error is HttpRequestException))
        {
            return errorRegion is null
                ? new LiveTransientFailure()
                : new LiveFragment(LiveErrorView.Create(new(errorRegion, DatabaseResults.Status(error), DatabaseResults.Message(error))), true);
        }
    }

    private static bool IsTransientStatus(int status) => status is StatusCodes.Status401Unauthorized or 408 or 409 or 429 or >= 500 and <= 599;

    public async Task<IResult> StreamAsync(HttpContext http, Func<CancellationToken, Task<LiveReadOutcome>> read,
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

    private async Task<IResult> StreamAsync(HttpContext http, LiveLease lease, Func<CancellationToken, Task<LiveReadOutcome>> read,
        IAsyncEnumerable<DateTimeOffset> changes, CancellationToken cancellationToken)
    {
        using var held = lease;
        var aborted = http.RequestAborted;
        using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(15), time);
        var stopping = applicationLifetime.ApplicationStopping;
        using var stream = CancellationTokenSource.CreateLinkedTokenSource(aborted, cancellationToken, lifetime.Token, stopping);
        streaming.Value = true;
        http.RequestAborted = stream.Token;
        try
        {
            Csrf.EnsureCookie(http);
            http.Response.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers["X-Accel-Buffering"] = "no";
            await DatastarEvents.KeepAliveAsync(http, stream.Token);
            string? previous = null;
            var lastWrite = time.GetTimestamp();
            var backoff = PollInterval;
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

                var outcome = await reading;
                var complete = outcome switch
                {
                    LiveFragment fragment => await WriteFragmentAsync(fragment, stream.Token),
                    LiveTransientFailure => await WaitToReadAsync(stream.Token),
                    LiveUnauthenticated => await WriteAuthAsync(StatusCodes.Status401Unauthorized, stream.Token),
                    LiveMfaRequired => await WriteAuthAsync(StatusCodes.Status403Forbidden, stream.Token),
                };
                if (complete)
                {
                    break;
                }
            }

            async Task<bool> WriteFragmentAsync(LiveFragment fragment, CancellationToken cancellationToken)
            {
                backoff = PollInterval;
                fragment.Slice.HttpContext = http;
                fragment.Slice.ServiceProvider = http.RequestServices;
                var html = await fragment.Slice.RenderAsync(cancellationToken: cancellationToken);
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(html)));
                if (hash != previous)
                {
                    await DatastarEvents.PatchElementsAsync(http, html, cancellationToken);
                    previous = hash;
                    lastWrite = time.GetTimestamp();
                }
                else if (time.GetElapsedTime(lastWrite) >= KeepAliveInterval)
                {
                    await DatastarEvents.KeepAliveAsync(http, cancellationToken);
                    lastWrite = time.GetTimestamp();
                }

                if (fragment.Complete)
                {
                    await DatastarEvents.LiveCompleteAsync(http, cancellationToken);
                }

                return fragment.Complete;
            }

            async Task<bool> WaitToReadAsync(CancellationToken cancellationToken)
            {
                if (time.GetElapsedTime(lastWrite) + backoff >= KeepAliveInterval)
                {
                    await DatastarEvents.KeepAliveAsync(http, cancellationToken);
                    lastWrite = time.GetTimestamp();
                }

                await Task.Delay(backoff, time, cancellationToken);
                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxReadBackoff.Ticks));
                return false;
            }

            async Task<bool> WriteAuthAsync(int status, CancellationToken cancellationToken)
            {
                await DatastarEvents.LiveAuthAsync(http, status, cancellationToken);
                return true;
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
