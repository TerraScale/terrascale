using System.Collections.Immutable;
using System.Diagnostics.Metrics;
using System.Text.Json;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Engine.Cluster;
using TerraBase.Hosting;
using TerraBase.Primitives;
using ZLogger;

namespace TerraBase.Features.Replication;

/// <summary>
/// Whether this machine's catalog may be trusted for serving (docs/terrabase/system-catalog-invariants.md section 2a): a
/// catalog restored from an older copy reads removed entries live (revoked keys, removed members, deleted ids) until it is
/// repaired, and nothing on this machine can tell a restored file from a current one. So every start is unverified until
/// this machine has pulled the catalog from every other started machine once.
/// </summary>
public interface ICatalogGate
{
    /// <summary>Whether the catalog is verified (or an operator opened it): requests that read it for authorization run.</summary>
    bool IsOpen { get; }

    /// <summary>
    /// Opens the gate without the missing verifications, on an operator's explicit request with a reason, and records it:
    /// a warning log and a catalog document <c>machines/&lt;machine&gt;/catalog_gate_overrides/&lt;ULID&gt;</c> naming the reason
    /// and the machines not verified. Returns the machines not verified.
    /// </summary>
    Task<ImmutableArray<string>> OverrideAsync(string reason, CancellationToken cancellationToken);
}

/// <summary>
/// The restore gate. Closed at every start; open once a forced anti-entropy pull of <c>_system</c> (every differing bucket,
/// at once) completed from every other machine the member directory lists as started, read successfully after this start
/// (or the members a static cell lists). A failed directory read or an unreachable listed machine keeps it closed, however
/// long: only an operator opens it then (<see cref="OverrideAsync"/>, audited). Once open it stays open until the next start.
/// </summary>
internal sealed class CatalogGate : BackgroundService, ICatalogGate
{
    private static readonly TimeSpan Pass = TimeSpan.FromSeconds(1);

    // The first pass runs before the listeners start; past this, the machine starts closed and the passes go on.
    private static readonly TimeSpan StartBudget = TimeSpan.FromSeconds(5);

    private readonly IMembership _membership;
    private readonly IMemberDirectory? _directory;
    private readonly AntiEntropy _antiEntropy;
    private readonly IStorageEngine _engine;
    private readonly TerraBaseOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<CatalogGate> _logger;
    private readonly HashSet<string> _verified = [with(StringComparer.Ordinal)];
    private readonly SemaphoreSlim _pass = new(1, 1);
    private volatile bool _open;

    public CatalogGate(
        IMembership membership, AntiEntropy antiEntropy, IStorageEngine engine, TerraBaseOptions options, TimeProvider time, IMeterFactory meters,
        ILogger<CatalogGate> logger, IMemberDirectory? directory = null)
    {
        (_membership, _directory, _antiEntropy, _engine, _options, _time, _logger) = (membership, directory, antiEntropy, engine, options, time, logger);
        meters.Create(Telemetry.MeterName).CreateObservableGauge("terrabase.catalog.verified", () => _open ? 1 : 0,
            description: "1 once this machine's catalog was pulled from every started machine since it started (or an operator opened it), else 0.");
    }

    public bool IsOpen => _open;

    /// <summary>The listed machines not verified yet, as of the last pass.</summary>
    public ImmutableArray<string> Pending { get; private set; } = [];

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(StartBudget);
        try
        {
            _ = await VerifyAsync(budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Out of start budget: closed until a pass verifies it.
        }

        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_open)
        {
            return;
        }

        using var timer = new PeriodicTimer(Pass, _time);
        while (!_open && await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            _ = await VerifyAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>One pass: pulls from every listed machine not verified yet; whether the gate is open after it.</summary>
    internal async Task<bool> VerifyAsync(CancellationToken cancellationToken)
    {
        await _pass.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_open)
            {
                return true;
            }

            if (await ListedAsync(cancellationToken).ConfigureAwait(false) is not { } peers)
            {
                return false;
            }

            foreach (var peer in peers.Where(peer => !_verified.Contains(peer.Name)))
            {
                try
                {
                    _ = await _antiEntropy.PullAsync(ChangeGroups.System, peer, cancellationToken).ConfigureAwait(false);
                    _ = _verified.Add(peer.Name);
                }
                catch (Exception error) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.ZLogWarning(error, $"The catalog could not be pulled from machine {peer.Name:@Peer} in {peer.Region:@Region}; this machine stays closed and tries again");
                }
            }

            Pending = [.. peers.Select(peer => peer.Name).Where(name => !_verified.Contains(name))];
            if (Pending.IsEmpty)
            {
                _open = true;
                _logger.ZLogInformation($"The catalog is verified against {peers.Count} other machines; this machine serves");
            }

            return _open;
        }
        finally
        {
            _pass.Release();
        }
    }

    public async Task<ImmutableArray<string>> OverrideAsync(string reason, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        await _pass.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var pending = _open ? [] : Pending;
            using var body = new MemoryStream();
            await using (var json = new Utf8JsonWriter(body))
            {
                json.WriteStartObject();
                json.WriteString("reason", reason);
                json.WriteString("at", _time.GetUtcNow());
                json.WriteBoolean("was_open", _open);
                json.WriteStartArray("unverified");
                foreach (var machine in pending)
                {
                    json.WriteStringValue(machine);
                }

                json.WriteEndArray();
                json.WriteEndObject();
            }

            var store = await _engine.OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false);
            await using (var transaction = await store.BeginTransactionAsync(write: true, cancellationToken).ConfigureAwait(false))
            {
                _ = await transaction.PutAsync($"machines/{_options.MachineId}/catalog_gate_overrides", EntityIds.New().ToString(), body.ToArray(), cancellationToken)
                    .ConfigureAwait(false);
                _ = await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            _logger.ZLogWarning($"An operator opened the catalog gate without verifying machines [{string.Join(", ", pending):@Unverified}]: {reason:@Reason}");
            _open = true;
            return pending;
        }
        finally
        {
            _pass.Release();
        }
    }

    // The other machines to verify against, or null while the list cannot be trusted (a failed directory read).
    private async Task<List<ClusterMember>?> ListedAsync(CancellationToken cancellationToken)
    {
        ImmutableArray<ClusterMember> members;
        if (_directory is null)
        {
            var view = await _membership.RefreshAsync(cancellationToken).ConfigureAwait(false);
            members = [.. view.Members.Concat(view.Elsewhere.IsDefault ? [] : view.Elsewhere)];
        }
        else
        {
            try
            {
                members = await _directory.ListAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.ZLogWarning(error, $"The member directory could not be read; this machine stays closed and tries again");
                return null;
            }
        }

        return [.. members.Where(member => member.Name != _membership.Self.Name && member.PeerAddress.Length > 0)];
    }

    public override void Dispose()
    {
        _pass.Dispose();
        base.Dispose();
    }
}

/// <summary>The restore gate on the request pipeline.</summary>
internal static class CatalogGateExtensions
{
    /// <summary>
    /// While the gate is closed, every request to an endpoint that authorizes from the catalog (any that is not anonymous)
    /// answers 503 <c>catalog_unverified</c> before authentication. Probes, metrics, status and the peers' gRPC still run.
    /// </summary>
    public static WebApplication UseTerraBaseCatalogGate(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var gate = app.Services.GetRequiredService<ICatalogGate>();
        app.Use((context, next) =>
            gate.IsOpen || context.GetEndpoint() is not { } endpoint
            || endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAuthorizeData>() is null
            || endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAllowAnonymous>() is not null
                ? next(context)
                : Closed(context));
        return app;
    }

    public static Task Closed(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        return BadRequests.WriteAsync(context, StatusCodes.Status503ServiceUnavailable, "catalog_unverified",
            "this machine's catalog is not verified against the other machines yet", op: null);
    }
}
