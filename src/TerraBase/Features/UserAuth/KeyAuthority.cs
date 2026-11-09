using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using TerraBase.Configuration;
using TerraBase.Errors;
using TerraBase.Hosting;

namespace TerraBase.Features.UserAuth;

public sealed record KeyToken(string KeyId, string Generation, string? ProjectId, string Token)
{
    public DateTimeOffset ValidTo { get; init; }

    public static bool IsGeneration(string? value) => value is { Length: 16 } && value.All(char.IsAsciiHexDigitLower);
}

public sealed record KeyActive(DateTimeOffset Until);

public sealed record KeyRefused(string Code);

public readonly union KeyAuthorityResult(KeyActive, KeyRefused, Unavailable);

public interface IKeyAuthority
{
    ValueTask<KeyAuthorityResult> CheckAsync(KeyToken key, CancellationToken cancellationToken);

    ValueTask<KeyAuthorityResult> AdmitAsync(KeyToken key, CancellationToken cancellationToken);
}

public sealed class KeyWatch : IDisposable
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(250);
    private readonly CancellationTokenSource _stop = new();
    private readonly PeriodicTimer _timer;

    private int _ended;
    private volatile bool _disposed;

    private KeyWatch(TimeProvider time)
    {
        _timer = new PeriodicTimer(Poll, time);
    }

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

    public KeyRefused? Refusal { get; private set; }

    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
        if (Interlocked.Exchange(ref _ended, 1) == 0)
        {
            try
            {
                _stop.Cancel();
            }
            catch (ObjectDisposedException)
            {
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
        }
    }
}

public static class KeyAuthorityMessages
{
    public static string Refused(string code) => code switch
    {
        "key_expired" => "the API key has expired",
        "key_revoked" => "the API key was revoked or rotated, or its project was deleted",
        _ => "the access token is not valid",
    };

    public const string Unavailable = "API key status cannot be verified right now; retry";

    public const string KeyAsBearer = "This is an API key; exchange it at the token endpoint and send the short-lived access token instead.";
}

internal sealed record TsAuthKeyStatus([property: JsonPropertyName("key_id")] string? KeyId);
internal sealed record TsAuthErrorDetail(string? Code);
internal sealed record TsAuthErrorBody(TsAuthErrorDetail? Error);

internal sealed class TsAuthKeyAuthority(TerraBaseOptions options, IHttpClientFactory http, TimeProvider time, IHostApplicationLifetime lifetime)
    : BackgroundService, IKeyAuthority
{
    public static readonly TimeSpan Fresh = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan RefreshAfter = Fresh / 2;

    public static readonly TimeSpan Admission = Fresh - RefreshAfter;

    private static readonly TimeSpan AdmissionRefresh = RefreshAfter / 2;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);
    private const int Capacity = 16 * 1024;
    private const int MaxInFlight = 256;
    private const int MaxResponseBytes = 16 * 1024;

    private sealed record Answer(KeyAuthorityResult Result, DateTimeOffset AskedAt);

    private readonly ConcurrentDictionary<string, Answer> _answers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<Answer>>> _asking = new(StringComparer.Ordinal);
    private readonly Lock _warmGate = new();
    private readonly Dictionary<string, KeyToken> _warm = new(StringComparer.Ordinal);
    private int _inFlight;

    public async ValueTask<KeyAuthorityResult> AdmitAsync(KeyToken key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        var id = Id(key);
        KeepWarm(id, key);
        if (TryAnswer(id, key, fresh: RefreshAfter, refreshAt: AdmissionRefresh, out var known))
        {
            return known;
        }

        for (var attempt = 0; ; attempt++)
        {
            var answer = await Ask(id, key).WaitAsync(cancellationToken).ConfigureAwait(false);
            KeyAuthorityResult result = answer.Result.Value is KeyActive active && active.Until - time.GetUtcNow() < Admission
                ? new Unavailable("ts-auth answered too slowly for new work to outlast its answer")
                : answer.Result;
            if (result.Value is not Unavailable || attempt == 1)
            {
                return result;
            }
        }
    }

    public async ValueTask<KeyAuthorityResult> CheckAsync(KeyToken key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        var id = Id(key);
        KeepWarm(id, key);
        return TryAnswer(id, key, fresh: Fresh, refreshAt: RefreshAfter, out var known)
            ? known
            : (await Ask(id, key).WaitAsync(cancellationToken).ConfigureAwait(false)).Result;
    }

    private bool TryAnswer(string id, KeyToken key, TimeSpan fresh, TimeSpan refreshAt, out KeyAuthorityResult answer)
    {
        answer = default;
        if (!_answers.TryGetValue(id, out var known))
        {
            return false;
        }

        var age = time.GetUtcNow() - known.AskedAt;
        if (known.Result.Value is not KeyRefused && age >= fresh)
        {
            return false;
        }

        if (known.Result.Value is KeyActive && age >= refreshAt)
        {
            _ = Ask(id, key);
        }

        answer = known.Result;
        return true;
    }

    private static string Id(KeyToken key) => key.KeyId + " " + key.Generation + " " + key.ProjectId;

    private void KeepWarm(string id, KeyToken key)
    {
        if (key.ValidTo <= time.GetUtcNow())
        {
            return;
        }

        lock (_warmGate)
        {
            if (_warm.TryGetValue(id, out var previous) ? key.ValidTo > previous.ValidTo : _warm.Count < MaxInFlight)
            {
                _warm[id] = key;
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(AdmissionRefresh / 2, time);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            KeyValuePair<string, KeyToken>[] active;
            var now = time.GetUtcNow();
            lock (_warmGate)
            {
                foreach (var (id, key) in _warm.ToArray())
                {
                    if (key.ValidTo <= now || _answers.TryGetValue(id, out var answer) && answer.Result.Value is KeyRefused)
                    {
                        _warm.Remove(id);
                    }
                }

                active = [.. _warm];
            }

            await Task.WhenAll(active.Where(pair => !_answers.TryGetValue(pair.Key, out var answer)
                    || now - answer.AskedAt >= AdmissionRefresh)
                .Select(pair => Ask(pair.Key, pair.Value))).ConfigureAwait(false);
        }
    }

    private Task<Answer> Ask(string id, KeyToken key) =>
        _asking.GetOrAdd(id, _ => new Lazy<Task<Answer>>(() => AskAsync(id, key))).Value;

    private async Task<Answer> AskAsync(string id, KeyToken key)
    {
        var answer = new Answer(new Unavailable("ts-auth key status is unavailable"), time.GetUtcNow());
        try
        {
            answer = answer with { Result = await RequestAsync(key, answer.AskedAt + Fresh).ConfigureAwait(false) };
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

            var issuer = new Uri(configured);
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(issuer.GetLeftPart(UriPartial.Authority)
                + "/v1" + issuer.AbsolutePath.TrimEnd('/') + "/api-keys/self"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Token);
            using var client = http.CreateClient(TsAuthKeys.HttpClientName);
            client.MaxResponseContentBufferSize = MaxResponseBytes;
            using var deadline = new CancellationTokenSource(Timeout, time);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, lifetime.ApplicationStopping);
            using var response = await client.SendAsync(request, cancellation.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var status = await response.Content.ReadFromJsonAsync(TerraBaseJsonContext.Default.TsAuthKeyStatus, cancellation.Token).ConfigureAwait(false);
                return status?.KeyId == key.KeyId ? new KeyActive(until) : new Unavailable("ts-auth answered about another key");
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new KeyActive(until);
            }

            if (response.StatusCode != HttpStatusCode.Unauthorized)
            {
                return new Unavailable("ts-auth key status is unavailable");
            }

            var body = await response.Content.ReadFromJsonAsync(TerraBaseJsonContext.Default.TsAuthErrorBody, cancellation.Token).ConfigureAwait(false);
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
