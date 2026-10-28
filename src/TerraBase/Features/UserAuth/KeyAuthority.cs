using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using TerraBase.Configuration;
using TerraBase.Errors;
using TerraBase.Hosting;

namespace TerraBase.Features.UserAuth;

/// <summary>An access token ts-auth issued for an API key, as its <c>key_id</c>, <c>key_gen</c> and <c>project_id</c> claims name it.</summary>
/// <param name="KeyId">The key (the token's <c>sub</c>).</param>
/// <param name="Generation">Which of the key's strings the token came from.</param>
/// <param name="ProjectId">The project the token acts in, if any.</param>
/// <param name="Token">The access token itself: ts-auth's status check takes it as its own credential.</param>
public sealed record KeyToken(string KeyId, string Generation, string? ProjectId, string Token)
{
    /// <summary>Whether <paramref name="value"/> has the shape of a <c>key_gen</c> claim: 16 lowercase hex digits.</summary>
    public static bool IsGeneration(string? value) => value is { Length: 16 } && value.All(char.IsAsciiHexDigitLower);
}

/// <summary>The key's tokens still act, until <paramref name="Until"/> unless asked again: the question's send time plus the answer's lifetime.</summary>
public sealed record KeyActive(DateTimeOffset Until);

/// <summary>ts-auth refuses the key's tokens: <c>key_revoked</c>, <c>key_expired</c>, or <c>invalid_token</c> for this token only.</summary>
public sealed record KeyRefused(string Code);

/// <summary>The key still acts, is refused, or ts-auth is unavailable (refuse too: fail closed).</summary>
public readonly union KeyAuthorityResult(KeyActive, KeyRefused, Unavailable);

/// <summary>
/// Whether an API key, and the key string and project a token of it was issued for, still act right now
/// (docs/terrabase/key-revocation.md). Every transport asks before it serves a key's request or statement.
/// </summary>
public interface IKeyAuthority
{
    /// <summary>Whether <paramref name="key"/> still acts; <see cref="Unavailable"/> must be refused as well.</summary>
    ValueTask<KeyAuthorityResult> CheckAsync(KeyToken key, CancellationToken cancellationToken);
}

/// <summary>
/// Keeps asking about a key while something runs on its authority (a request, a Postgres session) and cancels it once
/// the key is refused or cannot be verified, or once the last yes runs out before a new one arrives: running work never
/// outlives <see cref="KeyActive.Until"/>, so it is cut off at most <see cref="TsAuthKeyAuthority.Fresh"/> plus one poll
/// after ts-auth acknowledged a revocation.
/// </summary>
public sealed class KeyWatch : IDisposable
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(250);
    private readonly CancellationTokenSource _stop = new();
    private readonly PeriodicTimer _timer;

    // Whichever of Dispose and the watch loop ends second disposes _stop.
    private int _ended;
    private volatile bool _disposed;

    private KeyWatch(TimeProvider time)
    {
        _timer = new PeriodicTimer(Poll, time);
    }

    /// <summary>
    /// Watches the key <paramref name="current"/> names (it may renew the token; null refuses) until disposed, starting
    /// from the yes that admitted the work (<paramref name="until"/>, when known). Null (nothing to watch) when
    /// <paramref name="watched"/> is false.
    /// </summary>
    public static KeyWatch? Start(
        IKeyAuthority authority, bool watched, DateTimeOffset? until, Func<CancellationToken, ValueTask<KeyToken?>> current, CancellationTokenSource target, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(authority);
        if (!watched)
        {
            return null;
        }

        var watch = new KeyWatch(time);
        _ = watch.RunAsync(authority, until, current, target, time);
        return watch;
    }

    /// <summary>ts-auth's refusal that ended the watch; null while it runs, or when the key could not be verified in time.</summary>
    public KeyRefused? Refusal { get; private set; }

    /// <summary>Stops watching; the target is left as it is.</summary>
    public void Dispose()
    {
        _disposed = true;
        // Disposing a pending timer wait completes it with false, without a cancellation exception on every short request.
        _timer.Dispose();
        if (Interlocked.Exchange(ref _ended, 1) == 0)
        {
            try
            {
                _stop.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The loop ended meanwhile and disposed it: there is nothing left to stop.
            }
        }
        else
        {
            _stop.Dispose();
        }
    }

    private async Task RunAsync(
        IKeyAuthority authority, DateTimeOffset? until, Func<CancellationToken, ValueTask<KeyToken?>> current, CancellationTokenSource target, TimeProvider time)
    {
        try
        {
            while (await _timer.WaitForNextTickAsync().ConfigureAwait(false))
            {
                // The question must be answered before the last yes runs out; waiting does not extend it.
                using var deadline = until is { } end ? new CancellationTokenSource(end - time.GetUtcNow() is { Ticks: > 0 } left ? left : TimeSpan.Zero, time) : new CancellationTokenSource();
                using var asking = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, deadline.Token);
                var answer = await current(asking.Token).ConfigureAwait(false) is { } key
                    ? (await authority.CheckAsync(key, asking.Token).ConfigureAwait(false)).Value
                    : null;
                if (answer is not KeyActive active)
                {
                    Refusal = answer as KeyRefused;
                    break;
                }

                until = active.Until;
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            return;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // The last yes ran out first, or the check failed: either way the key is not verified, so the work ends.
        }
        finally
        {
            _timer.Dispose();
            if (Interlocked.Exchange(ref _ended, 1) == 1)
            {
                _stop.Dispose();
            }
        }

        try
        {
            if (!_disposed)
            {
                await target.CancelAsync().ConfigureAwait(false);
            }
        }
        catch (ObjectDisposedException)
        {
            // The target ended first.
        }
    }
}

/// <summary>What every transport says when it refuses a key.</summary>
public static class KeyAuthorityMessages
{
    /// <summary>The refusal ts-auth gave, for clients.</summary>
    public static string Refused(string code) => code switch
    {
        "key_expired" => "the API key has expired",
        "key_revoked" => "the API key was revoked or rotated, or its project was deleted",
        _ => "the access token is not valid",
    };

    /// <summary>ts-auth did not answer: the key is refused until it does.</summary>
    public const string Unavailable = "API key status cannot be verified right now; retry";
}

internal sealed record TsAuthKeyStatus([property: JsonPropertyName("key_id")] string? KeyId);
internal sealed record TsAuthErrorDetail(string? Code);
internal sealed record TsAuthErrorBody(TsAuthErrorDetail? Error);

/// <summary>
/// Asks ts-auth's <c>GET /v1/{tenant}/api-keys/self</c> with the key's token. An answer that the key acts holds for
/// <see cref="Fresh"/> from when the question was sent, and is asked again in the background after half of that, so a
/// busy key does not wait; a refusal holds for good (key ids are ULIDs, never reused; a retired key string or a deleted
/// project never comes back). One question per key at a time. ts-auth unreachable, slow or failing answers
/// <see cref="Unavailable"/>, never a stale yes: a key is refused at most <see cref="Fresh"/> after ts-auth acknowledged
/// its revocation, in every region.
/// </summary>
internal sealed class TsAuthKeyAuthority(TerraBaseOptions options, IHttpClientFactory http, TimeProvider time) : IKeyAuthority
{
    /// <summary>How long ts-auth's yes holds, from when it was asked: the revocation bound.</summary>
    public static readonly TimeSpan Fresh = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan RefreshAfter = Fresh / 2;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);
    private const int Capacity = 16 * 1024;
    private const int MaxInFlight = 256;
    private const int MaxResponseBytes = 16 * 1024;

    private sealed record Answer(KeyAuthorityResult Result, DateTimeOffset AskedAt);

    private readonly ConcurrentDictionary<string, Answer> _answers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<Answer>>> _asking = new(StringComparer.Ordinal);
    private int _inFlight;

    public async ValueTask<KeyAuthorityResult> CheckAsync(KeyToken key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        var id = key.KeyId + " " + key.Generation + " " + key.ProjectId;
        if (_answers.TryGetValue(id, out var known))
        {
            var age = time.GetUtcNow() - known.AskedAt;
            if (known.Result.Value is KeyRefused || age < Fresh)
            {
                if (known.Result.Value is KeyActive && age >= RefreshAfter)
                {
                    _ = Ask(id, key);
                }

                return known.Result;
            }
        }

        return (await Ask(id, key).WaitAsync(cancellationToken).ConfigureAwait(false)).Result;
    }

    private Task<Answer> Ask(string id, KeyToken key) =>
        _asking.GetOrAdd(id, _ => new Lazy<Task<Answer>>(() => AskAsync(id, key))).Value;

    private async Task<Answer> AskAsync(string id, KeyToken key)
    {
        var answer = new Answer(new Unavailable("ts-auth key status is unavailable"), time.GetUtcNow());
        try
        {
            answer = answer with { Result = await RequestAsync(key, answer.AskedAt + Fresh).ConfigureAwait(false) };
            // invalid_token concerns this token alone (another of the same key may still act); Unavailable never replaces a yes.
            if (answer.Result.Value is KeyActive or KeyRefused { Code: not "invalid_token" })
            {
                if (_answers.Count >= Capacity)
                {
                    _answers.Clear();
                }

                _answers.AddOrUpdate(id, answer, (_, previous) =>
                    previous.Result.Value is KeyRefused || previous.AskedAt > answer.AskedAt && answer.Result.Value is KeyActive ? previous : answer);
            }

            return answer;
        }
        finally
        {
            _asking.TryRemove(id, out _);
        }
    }

    private async Task<KeyAuthorityResult> RequestAsync(KeyToken key, DateTimeOffset until)
    {
        if (options.AuthIssuer is not { } configured || key.Token.Length is 0 or > 8192)
        {
            return new Unavailable("ts-auth is not configured");
        }

        try
        {
            if (Interlocked.Increment(ref _inFlight) > MaxInFlight)
            {
                return new Unavailable("too many ts-auth key status checks in flight");
            }

            // Only the configured ts-auth origin receives the token. No redirects; bounded responses and time.
            var issuer = new Uri(configured);
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(issuer.GetLeftPart(UriPartial.Authority)
                + "/v1" + issuer.AbsolutePath.TrimEnd('/') + "/api-keys/self"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Token);
            using var client = http.CreateClient(TsAuthKeys.HttpClientName);
            client.MaxResponseContentBufferSize = MaxResponseBytes;
            using var deadline = new CancellationTokenSource(Timeout, time);
            using var response = await client.SendAsync(request, deadline.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                // Only an answer about this very key counts as a yes.
                var status = await response.Content.ReadFromJsonAsync(TerraBaseJsonContext.Default.TsAuthKeyStatus, deadline.Token).ConfigureAwait(false);
                return status?.KeyId == key.KeyId ? new KeyActive(until) : new Unavailable("ts-auth answered about another key");
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // A ts-auth that predates key status (deploy order must not matter): the token's own checks stand, as before.
                return new KeyActive(until);
            }

            if (response.StatusCode != HttpStatusCode.Unauthorized)
            {
                return new Unavailable("ts-auth key status is unavailable");
            }

            var body = await response.Content.ReadFromJsonAsync(TerraBaseJsonContext.Default.TsAuthErrorBody, deadline.Token).ConfigureAwait(false);
            return new KeyRefused(body?.Error?.Code is "key_revoked" or "key_expired" ? body.Error.Code : "invalid_token");
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or NotSupportedException)
        {
            return new Unavailable("ts-auth key status is unreadable");
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException)
        {
            return new Unavailable("ts-auth key status is unavailable");
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }
}
