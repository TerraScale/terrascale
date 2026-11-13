using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TerraBase.Configuration;
using TerraBase.Features.Replicas;
using TerraBase.Hosting;
using ZLogger;

namespace TerraBase.Features.Clock;

internal sealed record ClockReport(string Node, string Region, double? SkewMilliseconds, bool LocalWritesAllowed, DateTimeOffset SampledAt);

internal sealed record PeerClock(long UtcMilliseconds);

internal sealed class ClockTrust(TerraBaseOptions options, InternalCredential credential, TimeProvider time, IMeterFactory meters, ILogger<ClockTrust> logger)
    : BackgroundService
{
    internal const long BudgetMilliseconds = 50;
    internal const int RoundsToOpen = 2;
    private const int OtherRegionsEvery = 5;
    private const int ReportEvery = 5;
    private static readonly TimeSpan Round = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan AnswerFor = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan AskFor = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan KernelGrace = TimeSpan.FromSeconds(60);
    private static readonly IReadOnlyList<(Mode Mode, KeyValuePair<string, object?>[] Tags)> ModeTags =
        [.. Enum.GetValues<Mode>().Select(mode => (mode, new KeyValuePair<string, object?>[] { new("mode", Labels(mode).Mode), new("reason", Labels(mode).Reason) }))];
    private readonly TimeProvider _monotonic = TimeProvider.System;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, PeerAnswer> _answers = new(StringComparer.Ordinal);
    private readonly Meter _meter = meters.Create(Telemetry.MeterName);
    private Counter<long>? _steps;
    private long? _kernelFailingSince;
    private HttpClient? _http;
    private Sample? _last;
    private long _closedAt;
    private long _agreedAt;
    private int _agreeingRounds;
    private int _answering;
    private int _agreeing;
    private int _reported;
    private Mode _mode = Mode.Opening;

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
        var failedRounds = _meter.CreateCounter<long>("terrabase.clock.failed_rounds", "{round}", "Trust rounds that ended with an unexpected error; the loop goes on.");
        _ = _meter.CreateObservableGauge("terrabase.clock.skew", Skew, "ms");
        _ = _meter.CreateObservableGauge("terrabase.clock.local_writes_allowed", () => Trusted ? 1 : 0);
        _ = _meter.CreateObservableGauge("terrabase.clock.agreeing_rounds", () => Volatile.Read(ref _agreeingRounds), "{round}");
        _ = _meter.CreateObservableGauge("terrabase.clock.peers_answering", () => Volatile.Read(ref _answering), "{node}");
        _ = _meter.CreateObservableGauge("terrabase.clock.peers_agreeing", () => Volatile.Read(ref _agreeing), "{node}");
        _ = _meter.CreateObservableGauge("terrabase.clock.reported", () => Volatile.Read(ref _reported));
        _ = _meter.CreateObservableGauge("terrabase.clock.mode", Modes, description: "One for the clock trust mode this node is in (peers_agree, peers_lost, first_of_build, no_peers or closed) and, while closed, the reason.");
        await using var reports = new ClockReports(options);
        using var timer = new PeriodicTimer(Round, time);
        long round = 0;
        do
        {
            try
            {
                var begun = _monotonic.GetTimestamp();
                bool everyone;
                lock (_gate)
                {
                    everyone = !TrustedLocked() || _mode is Mode.FirstOfBuild or Mode.PeersLost;
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
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                failedRounds.Add(1);
                logger.ZLogWarning(error, $"Clock trust round {round:@Round} failed");
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
        }

        foreach (var stale in _answers.Where(pair => _monotonic.GetElapsedTime(pair.Value.At, now) > AnswerFor).Select(pair => pair.Key).ToList())
        {
            _ = _answers.Remove(stale);
        }

        var answered = _answers.Values.OfType<Answered>().ToList();
        var agreeing = answered.Count(answer => answer.Agrees);
        Volatile.Write(ref _answering, answered.Count);
        Volatile.Write(ref _agreeing, agreeing);
        var peers = configured == 0 ? Mode.NoPeers
            : answered.Count > 0 ? 2 * (agreeing + 1) > answered.Count + 1 ? Mode.PeersAgree : Mode.PeersDisagree
            : _answers.Count > 0 ? Mode.FirstOfBuild
            : _agreeingRounds >= RoundsToOpen ? Mode.PeersLost
            : Mode.NoContact;
        if (KernelFails(now))
        {
            Close(now, Mode.Kernel);
        }
        else if (peers is Mode.PeersDisagree or Mode.NoContact)
        {
            Close(now, peers);
        }
        else
        {
            Volatile.Write(ref _agreeingRounds, _agreeingRounds + 1);
            _agreedAt = now;
            Enter(_agreeingRounds >= RoundsToOpen ? peers : Mode.Opening);
        }
    }

    private bool TrustedLocked()
    {
        Check();
        var now = _monotonic.GetTimestamp();
        if (KernelFails(now))
        {
            Close(now, Mode.Kernel);
        }
        else if (_agreeingRounds > 0 && _monotonic.GetElapsedTime(_agreedAt, now) > AnswerFor)
        {
            Close(now, Mode.Stalled);
        }

        return _agreeingRounds >= RoundsToOpen;
    }

    private bool KernelFails(long now)
    {
        if (KernelClock.ErrorMilliseconds <= BudgetMilliseconds)
        {
            _kernelFailingSince = null;
            return false;
        }

        _kernelFailingSince ??= now;
        return _agreeingRounds < RoundsToOpen || _monotonic.GetElapsedTime(_kernelFailingSince.Value, now) >= KernelGrace;
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
                Close(after, Mode.Step);
            }
        }

        _last = new Sample(before, wall, after);
    }

    private void Close(long at, Mode reason)
    {
        _closedAt = at;
        Volatile.Write(ref _agreeingRounds, 0);
        _answers.Clear();
        Enter(reason);
    }

    private void Enter(Mode mode)
    {
        if (mode == _mode)
        {
            return;
        }

        _mode = mode;
        var (name, reason) = Labels(mode);
        logger.ZLogInformation($"Clock trust mode {name:@Mode}, reason {reason:@Reason}");
    }

    private IEnumerable<Measurement<int>> Modes()
    {
        Mode current;
        lock (_gate)
        {
            _ = TrustedLocked();
            current = _mode;
        }

        return ModeTags.Select(entry => new Measurement<int>(entry.Mode == current ? 1 : 0, entry.Tags));
    }

    private static (string Mode, string Reason) Labels(Mode mode) => mode switch
    {
        Mode.PeersAgree => ("peers_agree", "none"),
        Mode.PeersLost => ("peers_lost", "none"),
        Mode.FirstOfBuild => ("first_of_build", "none"),
        Mode.NoPeers => ("no_peers", "none"),
        Mode.Opening => ("closed", "opening"),
        Mode.Kernel => ("closed", "kernel"),
        Mode.Step => ("closed", "step"),
        Mode.PeersDisagree => ("closed", "peers_disagree"),
        Mode.NoContact => ("closed", "no_contact"),
        Mode.Stalled => ("closed", "stalled"),
        _ => throw new UnreachableException(),
    };

    private double Skew()
    {
        lock (_gate)
        {
            return TrustedLocked()
                ? _answers.Values.OfType<Answered>().Where(answer => answer.Agrees).Select(answer => Math.Abs(answer.OffsetMilliseconds) + answer.UncertaintyMilliseconds)
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
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return response.Content.Headers.ContentType?.MediaType == "application/json"
                    && await response.Content.ReadFromJsonAsync(TerraBaseJsonContext.Default.ErrorBody, timeout.Token).ConfigureAwait(false) is { Error.Code: BadRequests.UnknownRoute }
                    ? new RouteUnknown(_monotonic.GetTimestamp())
                    : null;
            }

            if (!response.IsSuccessStatusCode
                || await response.Content.ReadFromJsonAsync(ClockJsonContext.Default.PeerClock, timeout.Token).ConfigureAwait(false) is not { } clock)
            {
                return null;
            }

            var received = _monotonic.GetTimestamp();
            var half = _monotonic.GetElapsedTime(sent, received).TotalMilliseconds / 2;
            var offset = clock.UtcMilliseconds - (sentWall + half);
            var uncertainty = half + 1;
            return new Answered(received, offset, uncertainty, Math.Abs(offset) <= BudgetMilliseconds + uncertainty);
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private sealed record Sample(long Before, long Wall, long After);

    private closed record class PeerAnswer(long At);

    private sealed record class Answered(long At, double OffsetMilliseconds, double UncertaintyMilliseconds, bool Agrees) : PeerAnswer(At);

    private sealed record class RouteUnknown(long At) : PeerAnswer(At);

    private enum Mode
    {
        PeersAgree,
        PeersLost,
        FirstOfBuild,
        NoPeers,
        Opening,
        Kernel,
        Step,
        PeersDisagree,
        NoContact,
        Stalled,
    }
}
