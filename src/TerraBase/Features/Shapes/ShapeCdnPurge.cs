using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.Runtime;
using TerraBase.Features.Memory;
using TerraBase.Hosting;
using ZLogger;

namespace TerraBase.Features.Shapes;

internal sealed class ShapeCdnPurge : BackgroundService, IShapeCleanup
{
    internal const string HttpClientName = "cloudflare-api";

    internal const string Zone = "4cc466e19478c116fae7dc445a6d0911";

    internal static readonly Uri PurgeUri = new($"https://api.cloudflare.com/client/v4/zones/{Zone}/purge_cache");

    private const int DeleteConcurrency = 8;
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan LeaseMargin = TimeSpan.FromSeconds(30);
    private readonly IHttpClientFactory _http;
    private readonly string? _token;
    private readonly ShapeObjectStore? _objects;
    private readonly IShapeCleanupStore _cleanup;
    private readonly IShapePurgeBudget _budget;
    private readonly IMemoryBudget _memory;
    private readonly TimeProvider _time;
    private readonly ILogger<ShapeCdnPurge> _logger;
    private readonly string _instance = Ulid.NewUlid().ToString();
    private readonly SemaphoreSlim _drain = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly Counter<long> _purges;
    private readonly Counter<long> _attempts;
    private ShapeCleanupStatus _status = new(0, 0, 0, 0, 0, 0);
    private long _alertedParked;
    private bool _alertedOverdue;
    private bool _alertedSaturated;
    private bool _recovered;

    public ShapeCdnPurge(IHttpClientFactory http, string? token, ShapeObjectStore? objects, IShapeCleanupStore cleanup, IShapePurgeBudget budget,
        IMemoryBudget memory, TimeProvider time, IMeterFactory meters, ILogger<ShapeCdnPurge> logger)
    {
        _http = http;
        _token = token;
        _objects = objects;
        _cleanup = cleanup;
        _budget = budget;
        _memory = memory;
        _time = time;
        _logger = logger;
        var meter = meters.Create(Telemetry.MeterName);
        _purges = meter.CreateCounter<long>("terrabase.shape.cdn.purges",
            description: "Shape CDN handle prefixes by purge outcome: purged, retried or parked.");
        _attempts = meter.CreateCounter<long>("terrabase.shape.cleanup.attempts",
            description: "Shape cleanup attempts by stage (delete, purge) and outcome (success, retried, parked, deferred, unavailable).");
        _ = meter.CreateObservableGauge("terrabase.shape.cleanup.objects", () => Volatile.Read(ref _status).Objects,
            description: "Shape objects held in the durable cleanup inventory.");
        _ = meter.CreateObservableGauge("terrabase.shape.cleanup.pending", () => Volatile.Read(ref _status).Pending,
            description: "Shape objects whose cleanup is due and not yet acknowledged.");
        _ = meter.CreateObservableGauge("terrabase.shape.cleanup.parked", () => Volatile.Read(ref _status).Parked,
            description: "Due shape cleanup parked after a credential or policy refusal.");
        _ = meter.CreateObservableGauge("terrabase.shape.cleanup.saturated", () => Volatile.Read(ref _status).Saturated ? 1 : 0,
            description: "1 while the cleanup inventory refuses new shape publication.");
        _ = meter.CreateObservableGauge("terrabase.shape.cleanup.oldest_age", () => Age(Volatile.Read(ref _status)), unit: "s",
            description: "Age of the oldest due shape cleanup since it became due.");
    }

    public void Wake()
    {
        if (_wake.CurrentCount == 0)
        {
            try
            {
                _ = _wake.Release();
            }
            catch (SemaphoreFullException)
            {
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_objects is null)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var next = Interval;
            try
            {
                next = await DrainAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.ZLogError(error, $"Durable shape cleanup is unavailable; its inventory stays pending");
            }

            using var delay = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var timer = Task.Delay(next, _time, delay.Token);
            _ = await Task.WhenAny(timer, _wake.WaitAsync(delay.Token)).ConfigureAwait(false);
            await delay.CancelAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask<TimeSpan> DrainAsync(CancellationToken cancellationToken)
    {
        if (_objects is null)
        {
            return Interval;
        }

        await _drain.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_recovered)
            {
                await _cleanup.RecoverAsync(cancellationToken).ConfigureAwait(false);
                _recovered = true;
            }

            using var window = _memory.TryReserve(MemoryLane.Bulk, ShapeCleanupLimits.WindowBytes, null);
            if (window is null)
            {
                return Interval;
            }

            _ = await _cleanup.CollectAsync(Now, cancellationToken).ConfigureAwait(false);
            var lease = _instance + ":" + Ulid.NewUlid();
            var leased = await _cleanup.LeaseAsync(lease, Now, cancellationToken).ConfigureAwait(false);
            var open = leased.ToDictionary(item => item.Key, StringComparer.Ordinal);
            var until = _time.GetUtcNow() + ShapeCleanupLimits.Lease - LeaseMargin;
            var next = Interval;
            var (failures, deleted) = await DeleteAsync(leased.Where(item => item.Stage == ShapeCleanupStage.Fenced), until, cancellationToken).ConfigureAwait(false);
            next = await SaveAsync(lease, failures, open, next, cancellationToken).ConfigureAwait(false);
            _ = await _cleanup.SaveAsync(lease, [.. deleted.Select(item => new ShapeCleanupProgress(item.Key, ShapeCleanupStage.Deleted, 0, 0, Held: true))],
                Now, cancellationToken).ConfigureAwait(false);
            foreach (var item in deleted)
            {
                open[item.Key] = item;
            }

            var ready = open.Values.Where(item => item.Stage == ShapeCleanupStage.Deleted).ToList();
            if (!_objects.Cdn)
            {
                next = await SaveAsync(lease, [.. ready.Select(item => new ShapeCleanupProgress(item.Key, ShapeCleanupStage.Complete, 0, 0))], open, next,
                    cancellationToken).ConfigureAwait(false);
                ready.Clear();
            }

            foreach (var batch in ready.GroupBy(item => item.Handle, StringComparer.Ordinal).Chunk(ShapeCleanupLimits.PurgeBatch))
            {
                if (_time.GetUtcNow() >= until)
                {
                    break;
                }

                var (progress, stop) = await PurgeAsync([.. batch.Select(group => group.Key)], [.. batch.SelectMany(group => group)], cancellationToken)
                    .ConfigureAwait(false);
                next = await SaveAsync(lease, progress, open, next, cancellationToken).ConfigureAwait(false);
                if (stop)
                {
                    break;
                }
            }

            _ = await SaveAsync(lease, [.. open.Values.Select(item => new ShapeCleanupProgress(item.Key, item.Stage, item.Attempt, 0))], open, next,
                cancellationToken).ConfigureAwait(false);
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
            return leased.Count == ShapeCleanupLimits.WindowItems ? TimeSpan.Zero : next;
        }
        finally
        {
            _drain.Release();
        }
    }

    private async Task<(List<ShapeCleanupProgress> Progress, List<ShapeCleanupItem> Deleted)> DeleteAsync(IEnumerable<ShapeCleanupItem> fenced,
        DateTimeOffset until, CancellationToken cancellationToken)
    {
        List<ShapeCleanupProgress> progress = [];
        List<ShapeCleanupItem> deleted = [];
        var gate = new Lock();
        await Parallel.ForEachAsync(fenced, new ParallelOptions { MaxDegreeOfParallelism = DeleteConcurrency, CancellationToken = cancellationToken },
            async (item, token) =>
            {
                if (_time.GetUtcNow() >= until)
                {
                    return;
                }

                var outcome = await DeleteObjectAsync(item.Key, token).ConfigureAwait(false);
                ShapeCleanupProgress step = outcome switch
                {
                    0 => new(item.Key, ShapeCleanupStage.Deleted, 0, 0),
                    < 0 => Retry(item, null),
                    _ => Park(item, outcome),
                };
                _attempts.Add(1, Tag("delete", outcome == 0 ? "success" : outcome < 0 ? "retried" : "parked"));
                lock (gate)
                {
                    if (outcome == 0)
                    {
                        deleted.Add(item with { Stage = ShapeCleanupStage.Deleted, Attempt = 0 });
                    }
                    else
                    {
                        progress.Add(step);
                    }
                }
            }).ConfigureAwait(false);
        return (progress, deleted);
    }

    private async Task<int> DeleteObjectAsync(string key, CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30), _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            return await _objects!.DeleteAsync(key, linked.Token).ConfigureAwait(false) ? 0 : (int)HttpStatusCode.Forbidden;
        }
        catch (AmazonServiceException error) when (error.StatusCode == HttpStatusCode.NotFound)
        {
            return 0;
        }
        catch (AmazonServiceException error) when ((int)error.StatusCode is >= 400 and < 500
            && error.StatusCode is not (HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout))
        {
            _logger.ZLogError($"Shape object deletion was refused with status {(int)error.StatusCode}; cleanup is parked for credential or policy repair");
            return (int)error.StatusCode;
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested
            && error is AmazonServiceException or HttpRequestException or IOException or OperationCanceledException)
        {
            _logger.ZLogWarning(error, $"Shape object deletion failed; it is retried");
            return -1;
        }
    }

    private async Task<(List<ShapeCleanupProgress> Progress, bool Stop)> PurgeAsync(List<string> handles, List<ShapeCleanupItem> items,
        CancellationToken cancellationToken)
    {
        ShapePurgeSlot slot;
        try
        {
            slot = await _budget.TakeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            _attempts.Add(1, Tag("purge", "unavailable"));
            _logger.ZLogError(error, $"The shared CDN purge budget is unavailable; purges stay pending");
            return ([.. items.Select(item => new ShapeCleanupProgress(item.Key, item.Stage, item.Attempt, Now + (long)Interval.TotalMilliseconds))], true);
        }

        if (!slot.Granted)
        {
            _attempts.Add(1, Tag("purge", "deferred"));
            return ([.. items.Select(item => new ShapeCleanupProgress(item.Key, item.Stage, item.Attempt, slot.NotBefore.ToUnixTimeMilliseconds()))], true);
        }

        var (status, success, retryAfter) = await SendAsync(handles, cancellationToken).ConfigureAwait(false);
        if (success)
        {
            _attempts.Add(1, Tag("purge", "success"));
            _purges.Add(handles.Count, new KeyValuePair<string, object?>("outcome", "purged"));
            return ([.. items.Select(item => new ShapeCleanupProgress(item.Key, ShapeCleanupStage.Complete, 0, 0))], false);
        }

        if (status is >= 400 and < 500 and not (int)HttpStatusCode.TooManyRequests)
        {
            _attempts.Add(1, Tag("purge", "parked"));
            _purges.Add(handles.Count, new KeyValuePair<string, object?>("outcome", "parked"));
            _logger.ZLogError($"Shape CDN purge was refused with status {status}; cleanup is parked for credential or policy repair");
            return ([.. items.Select(item => Park(item, status))], true);
        }

        if (retryAfter is { } after)
        {
            await _budget.DeferAsync(_time.GetUtcNow() + after, cancellationToken).ConfigureAwait(false);
        }

        _attempts.Add(1, Tag("purge", "retried"));
        _purges.Add(handles.Count, new KeyValuePair<string, object?>("outcome", "retried"));
        return ([.. items.Select(item => Retry(item, retryAfter))], status == (int)HttpStatusCode.TooManyRequests);
    }

    private async Task<TimeSpan> SaveAsync(string lease, List<ShapeCleanupProgress> progress, Dictionary<string, ShapeCleanupItem> open, TimeSpan next,
        CancellationToken cancellationToken)
    {
        _ = await _cleanup.SaveAsync(lease, progress, Now, cancellationToken).ConfigureAwait(false);
        foreach (var item in progress)
        {
            open.Remove(item.Key);
        }

        return Earliest(next, progress);
    }

    private async Task<(int Status, bool Success, TimeSpan? RetryAfter)> SendAsync(List<string> handles, CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30), _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, PurgeUri)
            {
                Content = JsonContent.Create(new ShapeCdnPurgeRequest([.. handles.Select(ShapeCleanupLimits.Prefix)]),
                    ShapeCdnPurgeJsonContext.Default.ShapeCdnPurgeRequest),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            using var response = await _http.CreateClient(HttpClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token)
                .ConfigureAwait(false);
            var retryAfter = response.Headers.RetryAfter is { } header
                ? header.Delta ?? (header.Date is { } date ? date - _time.GetUtcNow() : null) : null;
            retryAfter = retryAfter is { } wait ? TimeSpan.FromTicks(Math.Clamp(wait.Ticks, 0, ShapeCleanupLimits.Cycle.Ticks)) : null;
            if (!response.IsSuccessStatusCode)
            {
                return ((int)response.StatusCode, false, retryAfter);
            }

            await response.Content.LoadIntoBufferAsync(64 * 1024, linked.Token).ConfigureAwait(false);
            var body = await response.Content.ReadFromJsonAsync(ShapeCdnPurgeJsonContext.Default.ShapeCdnPurgeResponse, linked.Token).ConfigureAwait(false);
            return ((int)response.StatusCode, body is { Success: true }, retryAfter);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested
            && error is HttpRequestException or OperationCanceledException or JsonException or IOException)
        {
            _logger.ZLogWarning(error, $"Shape CDN purge request failed; it is retried");
            return (0, false, null);
        }
    }

    private ShapeCleanupProgress Retry(ShapeCleanupItem item, TimeSpan? retryAfter)
    {
        var attempt = item.Attempt + 1;
        if (attempt >= ShapeCleanupLimits.Attempts)
        {
            var cycle = ShapeCleanupLimits.Cycle + TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * ShapeCleanupLimits.MaxBackoff.TotalMilliseconds);
            return new(item.Key, item.Stage, 0, Now + (long)Math.Max(cycle.TotalMilliseconds, retryAfter?.TotalMilliseconds ?? 0));
        }

        var backoff = Math.Min(ShapeCleanupLimits.MaxBackoff.TotalMilliseconds, 1000d * (1L << (attempt - 1))) * (0.5 + Random.Shared.NextDouble() / 2);
        return new(item.Key, item.Stage, attempt, Now + (long)Math.Max(backoff, retryAfter?.TotalMilliseconds ?? 0));
    }

    private static ShapeCleanupProgress Park(ShapeCleanupItem item, int status) => new(item.Key, item.Stage, item.Attempt, 0, status);

    private TimeSpan Earliest(TimeSpan current, IEnumerable<ShapeCleanupProgress> progress)
    {
        foreach (var item in progress.Where(item => item.Parked == 0 && item.Stage != ShapeCleanupStage.Complete))
        {
            var wait = TimeSpan.FromMilliseconds(Math.Max(0, item.NextAttempt - Now));
            current = wait < current ? wait : current;
        }

        return current < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : current;
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var status = await _cleanup.StatusAsync(Now, cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _status, status);
        var overdue = Age(status) > TimeSpan.FromHours(1).TotalSeconds;
        if (status.Parked > _alertedParked || overdue && !_alertedOverdue || status.Saturated && !_alertedSaturated)
        {
            _logger.ZLogError($"Durable shape cleanup needs attention: {status.Pending} due, {status.Parked} parked, oldest {Age(status):F0} s, saturated {status.Saturated}");
        }

        _alertedParked = status.Parked;
        _alertedOverdue = overdue;
        _alertedSaturated = status.Saturated;
    }

    private double Age(ShapeCleanupStatus status) => status.Pending == 0 ? 0 : Math.Max(0, (Now - status.OldestDue) / 1000d);

    private long Now => _time.GetUtcNow().ToUnixTimeMilliseconds();

    private static KeyValuePair<string, object?>[] Tag(string stage, string outcome) =>
        [new("stage", stage), new("outcome", outcome)];

    public override void Dispose()
    {
        base.Dispose();
        _drain.Dispose();
        _wake.Dispose();
    }
}

internal sealed record ShapeCdnPurgeRequest(IReadOnlyList<string> Prefixes);

internal sealed record ShapeCdnPurgeResponse(bool Success);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(ShapeCdnPurgeRequest))]
[JsonSerializable(typeof(ShapeCdnPurgeResponse))]
internal sealed partial class ShapeCdnPurgeJsonContext : JsonSerializerContext;
