using System.Diagnostics.Metrics;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Channels;
using TerraBase.Configuration;
using TerraBase.Features.HomeApply;
using TerraBase.Features.HomePush;
using TerraBase.Features.LocalAck;
using TerraBase.Features.Replicas;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using ZLogger;
using static TerraBase.Features.HomePush.HomePush;

namespace TerraBase.Features.LocalAck;

internal interface IInRegionAck
{
    bool PartnerHealthy { get; }

    bool QuorumHealthy { get; }

    ValueTask<bool> HoldAsync(TenantContext database, string home, ulong sequence, byte[] payload, CancellationToken cancellationToken);

    void Release(TenantContext database, ulong through);
}

internal sealed class InRegionAck(
    TerraBaseOptions options, IRegionPartners partners, IReplicaPeers homes, HoldStore holds, ITransactionCrypto crypto, InternalCredential credential, IMeterFactory meters,
    TimeProvider time, ILogger<InRegionAck> logger) : BackgroundService, IInRegionAck
{
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan HealthyWithin = TimeSpan.FromSeconds(3);
    internal static readonly TimeSpan OriginLostAfter = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HoldTimeout = TimeSpan.FromSeconds(2);
    private const int FinishPage = 64;

    private readonly Channel<HoldRelease> _releases = Channel.CreateBounded<HoldRelease>(
        new BoundedChannelOptions(1024) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly Meter _meter = meters.Create(Telemetry.MeterName);
    private HttpClient? _http;
    private volatile IReadOnlyList<RegionPartner>? _partners;
    private volatile RegionPartner? _answeredPartner;
    private long _lastAnswerTicks;
    private Counter<long>? _finished;
    private bool _durabilityLogged;

    public bool PartnerHealthy => _partners is [var partner, ..] && partner == _answeredPartner
        && time.GetUtcNow().UtcTicks - Interlocked.Read(ref _lastAnswerTicks) < HealthyWithin.Ticks;

    public bool QuorumHealthy => PartnerHealthy;

    private HttpClient Http => _http ??= PeerHttp.Create(credential);

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        _finished = _meter.CreateCounter<long>("terrabase.localack.finished", "{transaction}",
            "Held transactions this node applied at the home after their origin node was lost.");
        _ = _meter.CreateObservableGauge("terrabase.localack.held", () => holds.Count, "{transaction}", "Transactions this node holds for its in-region partner.");
        _ = _meter.CreateObservableGauge("terrabase.localack.partner", () => PartnerHealthy ? 1 : 0, "{node}", "1 while the in-region partner answers.");
        try
        {
            var members = await partners.ListAsync(cancellationToken).ConfigureAwait(false);
            _partners = members;
            LogDurability(members);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.ZLogInformation($"The in-region partner list is not ready; writes forward to the home until it is: {error.GetType().Name:@Error}");
        }

        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public override void Dispose()
    {
        _http?.Dispose();
        _meter.Dispose();
        base.Dispose();
    }

    public async ValueTask<bool> HoldAsync(TenantContext database, string home, ulong sequence, byte[] payload, CancellationToken cancellationToken)
    {
        var members = _partners;
        if (members is not [var partner, ..] || !PartnerHealthy)
        {
            return false;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(HoldTimeout);
        try
        {
            using var body = Json(JsonSerializer.SerializeToUtf8Bytes(
                new HeldTransaction(1, options.MachineId, home, database.OrganizationId, database.ProjectId, database.DatabaseId, sequence, payload),
                LocalAckJsonContext.Default.HeldTransaction));
            using var response = await Http.PostAsync(new Uri(partner.Internal, "v1/sync/holds"), body, timeout.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                Answered(partner);
                return true;
            }

            logger.ZLogWarning($"The in-region partner did not hold a local transaction ({(int)response.StatusCode}); the write waits for the home");
            return false;
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.ZLogWarning($"The in-region partner did not hold a local transaction ({error.GetType().Name:@Error}); the write waits for the home");
            return false;
        }
    }

    public void Release(TenantContext database, ulong through) =>
        _releases.Writer.TryWrite(new HoldRelease(options.MachineId, database.OrganizationId, database.ProjectId, database.DatabaseId, through));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) =>
        await Task.WhenAll(TouchAsync(stoppingToken), ReleasesAsync(stoppingToken)).ConfigureAwait(false);

    private async Task TouchAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var members = await partners.ListAsync(cancellationToken).ConfigureAwait(false);
                _partners = members;
                LogDurability(members);
                if (_partners is [var partner, ..])
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(HoldTimeout);
                    using var response = await Http.PostAsync(new Uri(partner.Internal, $"v1/sync/holds/{options.MachineId}/contact"), null, timeout.Token)
                        .ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                    {
                        Answered(partner);
                    }
                }

                foreach (var origin in await holds.OriginsAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (origin != options.MachineId && time.GetUtcNow() - holds.LastContact(origin) > OriginLostAfter)
                    {
                        await FinishAsync(origin, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _partners = null;
                logger.ZLogInformation($"The in-region partner check retries: {error.GetType().Name:@Error}");
            }

            await Task.Delay(Interval, time, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task FinishAsync(string origin, CancellationToken cancellationToken)
    {
        var stalled = new HashSet<(string, string, string)>();
        var peers = await homes.ListAsync(cancellationToken).ConfigureAwait(false);
        HeldTransaction? after = null;
        while (true)
        {
            var page = await holds.ListAsync(origin, after, FinishPage, cancellationToken).ConfigureAwait(false);
            foreach (var held in page)
            {
                after = held;
                var key = (held.Organization!, held.Project!, held.Database!);
                if (stalled.Contains(key))
                {
                    continue;
                }

                if (peers.FirstOrDefault(peer => peer.Region == held.Home) is not { } home || !await ApplyAsync(home, held, cancellationToken).ConfigureAwait(false))
                {
                    _ = stalled.Add(key);
                    continue;
                }

                await holds.DropAsync(held, cancellationToken).ConfigureAwait(false);
                _finished?.Add(1);
            }

            if (page.Count < FinishPage)
            {
                return;
            }
        }
    }

    private async Task<bool> ApplyAsync(ReplicaPeer home, HeldTransaction held, CancellationToken cancellationToken)
    {
        try
        {
            var database = new TenantContext(held.Organization!, PlanTier.Hobby, held.Project, held.Database);
            var plain = await crypto.OpenAsync(database, held.Payload!, cancellationToken).ConfigureAwait(false);
            if (plain is null || !TryDecode(plain, out var transaction))
            {
                return true;
            }
            using var body = Json(JsonSerializer.SerializeToUtf8Bytes(transaction, HomeApplyJsonContext.Default.RegionTransaction));
            using var response = await Http.PostAsync(new Uri(home.Internal, ReplicaRoutes.DatabasePath(database, "apply")[1..]), body, cancellationToken)
                .ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            logger.ZLogWarning($"The home did not apply a held transaction of a lost node ({(int)response.StatusCode}); it retries");
            return false;
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.ZLogWarning($"The home did not apply a held transaction of a lost node ({error.GetType().Name:@Error}); it retries");
            return false;
        }
    }

    private async Task ReleasesAsync(CancellationToken cancellationToken)
    {
        await foreach (var release in _releases.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (_partners is not [var partner, ..])
            {
                continue;
            }

            try
            {
                using var body = Json(JsonSerializer.SerializeToUtf8Bytes(release, LocalAckJsonContext.Default.HoldRelease));
                using var response = await Http.PostAsync(new Uri(partner.Internal, "v1/sync/holds/release"), body, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.ZLogInformation($"A hold release waits for the next one: {error.GetType().Name:@Error}");
            }
        }
    }

    private static ByteArrayContent Json(byte[] json)
    {
        var content = new ByteArrayContent(json);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    private void LogDurability(IReadOnlyList<RegionPartner> members)
    {
        if (_durabilityLogged)
        {
            return;
        }

        _durabilityLogged = true;
        if (members is [])
        {
            logger.ZLogInformation($"This region has no partner; writes forward to the home");
        }
        else
        {
            logger.ZLogInformation($"This region can acknowledge writes locally");
        }
    }

    private void Answered(RegionPartner partner)
    {
        Interlocked.Exchange(ref _lastAnswerTicks, time.GetUtcNow().UtcTicks);
        _answeredPartner = partner;
    }
}
