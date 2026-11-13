using System.Diagnostics.Metrics;
using System.Net.Http.Json;
using System.Text.Json;
using TerraBase.Configuration;
using TerraBase.Features.Replicas;
using TerraBase.Hosting;

namespace TerraBase.Features.Clock;

internal sealed record ClockReport(string Node, string Region, double? SkewMilliseconds, bool LocalWritesAllowed, DateTimeOffset SampledAt);

internal sealed record PeerClock(long UtcMilliseconds);

internal sealed class ClockTrust(TerraBaseOptions options, InternalCredential credential, TimeProvider time, IMeterFactory meters) : BackgroundService
{
    internal const long BudgetMilliseconds = 50;
    internal const int RoundsToOpen = 2;
    private const int OtherRegionsEvery = 5;
    private const int ReportEvery = 5;
    private static readonly TimeSpan Round = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan AnswerFor = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan AskFor = TimeSpan.FromSeconds(2);
    private readonly TimeProvider _monotonic = TimeProvider.System;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, PeerAnswer> _answers = new(StringComparer.Ordinal);
    private readonly Meter _meter = meters.Create(Telemetry.MeterName);
    private Counter<long>? _steps;
    private HttpClient? _http;
    private Sample? _last;
    private long _closedAt;
    private long _agreedAt;
    private int _agreeingRounds;
    private int _answering;
    private int _agreeing;
    private int _reported;

    public bool Trusted
    {
        get
        {
            lock (_gate)
            {
                return TrustedLocked();
            }
        }
    }

    private HttpClient Http => _http ??= PeerHttp.Create(credential);

    public override void Dispose()
    {
        _http?.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _steps = _meter.CreateCounter<long>("terrabase.clock.steps", "{step}", "Wall-clock steps this node detected against its monotonic clock.");
        _ = _meter.CreateObservableGauge("terrabase.clock.skew", Skew, "ms");
        _ = _meter.CreateObservableGauge("terrabase.clock.local_writes_allowed", () => Trusted ? 1 : 0);
        _ = _meter.CreateObservableGauge("terrabase.clock.agreeing_rounds", () => Volatile.Read(ref _agreeingRounds), "{round}");
        _ = _meter.CreateObservableGauge("terrabase.clock.peers_answering", () => Volatile.Read(ref _answering), "{node}");
        _ = _meter.CreateObservableGauge("terrabase.clock.peers_agreeing", () => Volatile.Read(ref _agreeing), "{node}");
        _ = _meter.CreateObservableGauge("terrabase.clock.reported", () => Volatile.Read(ref _reported));
        await using var reports = new ClockReports(options);
        using var timer = new PeriodicTimer(Round, time);
        long round = 0;
        do
        {
            var begun = _monotonic.GetTimestamp();
            bool everyone;
            lock (_gate)
            {
                everyone = !TrustedLocked();
            }

            var peers = await PeersAsync(stoppingToken).ConfigureAwait(false);
            var asked = peers.Where(peer => everyone || peer.Region == options.Region || round % OtherRegionsEvery == 0).ToList();
            var answers = await Task.WhenAll(asked.Select(peer => AskAsync(peer, stoppingToken))).ConfigureAwait(false);
            lock (_gate)
            {
                Finish(begun, peers.Count, asked, answers);
            }

            if (round % ReportEvery == 0)
            {
                var skew = Skew();
                var stored = await reports.PublishAsync(new ClockReport(options.MachineId, options.Region, double.IsFinite(skew) ? skew : null, Trusted, time.GetUtcNow()),
                    stoppingToken).ConfigureAwait(false);
                Volatile.Write(ref _reported, stored ? 1 : 0);
            }

            round++;
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private void Finish(long begun, int configured, List<ServingNode> asked, PeerAnswer?[] answers)
    {
        var now = _monotonic.GetTimestamp();
        Check();
        if (begun <= _closedAt)
        {
            return;
        }

        for (var i = 0; i < asked.Count; i++)
        {
            if (answers[i] is { } answer)
            {
                _answers[asked[i].Name] = answer;
            }
            else
            {
                _ = _answers.Remove(asked[i].Name);
            }
        }

        foreach (var stale in _answers.Where(pair => _monotonic.GetElapsedTime(pair.Value.At, now) > AnswerFor).Select(pair => pair.Key).ToList())
        {
            _ = _answers.Remove(stale);
        }

        var answering = _answers.Count;
        var agreeing = _answers.Values.Count(answer => answer.Agrees);
        Volatile.Write(ref _answering, answering);
        Volatile.Write(ref _agreeing, agreeing);
        var peersAgree = configured == 0 || (answering > 0 && 2 * (agreeing + 1) > answering + 1);
        if (peersAgree && KernelClock.ErrorMilliseconds <= BudgetMilliseconds)
        {
            Volatile.Write(ref _agreeingRounds, _agreeingRounds + 1);
            _agreedAt = now;
        }
        else
        {
            Close(now);
        }
    }

    private bool TrustedLocked()
    {
        Check();
        var now = _monotonic.GetTimestamp();
        if (KernelClock.ErrorMilliseconds > BudgetMilliseconds || (_agreeingRounds > 0 && _monotonic.GetElapsedTime(_agreedAt, now) > AnswerFor))
        {
            Close(now);
        }

        return _agreeingRounds >= RoundsToOpen;
    }

    private void Check()
    {
        var before = _monotonic.GetTimestamp();
        var wall = time.GetUtcNow().ToUnixTimeMilliseconds();
        var after = _monotonic.GetTimestamp();
        if (_last is { } last)
        {
            var moved = wall - last.Wall;
            var least = _monotonic.GetElapsedTime(last.After, before).TotalMilliseconds;
            var most = _monotonic.GetElapsedTime(last.Before, after).TotalMilliseconds;
            if (moved < least - BudgetMilliseconds - 1 || moved > most + BudgetMilliseconds + 1)
            {
                _steps?.Add(1);
                Close(after);
            }
        }

        _last = new Sample(before, wall, after);
    }

    private void Close(long at)
    {
        _closedAt = at;
        Volatile.Write(ref _agreeingRounds, 0);
        _answers.Clear();
    }

    private double Skew()
    {
        lock (_gate)
        {
            return TrustedLocked()
                ? _answers.Values.Where(answer => answer.Agrees).Select(answer => Math.Abs(answer.OffsetMilliseconds) + answer.UncertaintyMilliseconds)
                    .Append(KernelClock.ErrorMilliseconds).Max()
                : double.PositiveInfinity;
        }
    }

    private async Task<List<ServingNode>> PeersAsync(CancellationToken cancellationToken)
    {
        if (options.PeersFile is null)
        {
            return [];
        }

        var nodes = await new PeersFile(options).ListAsync(cancellationToken).ConfigureAwait(false);
        return [.. nodes.Where(node => node.Name != options.MachineId)];
    }

    private async Task<PeerAnswer?> AskAsync(ServingNode peer, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(AskFor);
        var sentWall = time.GetUtcNow().ToUnixTimeMilliseconds();
        var sent = _monotonic.GetTimestamp();
        try
        {
            using var response = await Http.GetAsync(new Uri(peer.InternalListener(options.InternalPort), "v1/sync/clock"), timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode
                || await response.Content.ReadFromJsonAsync(ClockJsonContext.Default.PeerClock, timeout.Token).ConfigureAwait(false) is not { } clock)
            {
                return null;
            }

            var received = _monotonic.GetTimestamp();
            var half = _monotonic.GetElapsedTime(sent, received).TotalMilliseconds / 2;
            var offset = clock.UtcMilliseconds - (sentWall + half);
            var uncertainty = half + 1;
            return new PeerAnswer(received, offset, uncertainty, Math.Abs(offset) <= BudgetMilliseconds + uncertainty);
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private sealed record Sample(long Before, long Wall, long After);

    private sealed record PeerAnswer(long At, double OffsetMilliseconds, double UncertaintyMilliseconds, bool Agrees);
}
