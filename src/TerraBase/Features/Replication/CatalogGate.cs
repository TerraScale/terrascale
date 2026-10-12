using System.Collections.Immutable;
using System.Diagnostics.Metrics;
using System.Text;
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
/// the catalog has been pulled from the other machines.
/// </summary>
public interface ICatalogGate
{
    /// <summary>Whether the catalog is verified (or bootstrapped as a new cluster's): requests that read it for authorization run.</summary>
    bool IsOpen { get; }
}

/// <summary>
/// The restore gate. Closed at every start. Opens once a forced anti-entropy pull of <c>_system</c> (every differing bucket,
/// at once) completed since this start from every other machine the member directory lists as started, and from at least one
/// machine of every other region the catalog records (<see cref="Members"/>), which covers a stopped machine that holds the
/// only copy of a change. No machine to pull from is unknown, not proof of a current catalog: the gate stays closed. Two
/// ways out of that, and nothing else: the cell is a single machine by configuration (no member directory, nothing listed,
/// no other region recorded), or an operator requests the bootstrap of a new cluster (<see cref="BootstrapFile"/>), which
/// succeeds only on a genesis catalog. A failed directory read or an unreachable machine keeps it closed, however long.
/// </summary>
internal sealed class CatalogGate : BackgroundService, ICatalogGate
{
    /// <summary>Every machine that ever ran this cluster's catalog: id = machine id, body = <c>{"region": …}</c>; replicated.</summary>
    internal const string Members = "cluster_members";

    /// <summary>
    /// The new-cluster bootstrap request: a file of that name in the data directory, holding the operator's reason, written
    /// through <c>fly ssh</c> (the operator's authenticated access to a machine). Consumed by the next pass, accepted or not.
    /// </summary>
    internal const string BootstrapFile = "catalog-bootstrap";

    /// <summary>Every bootstrap request a machine read: <c>{"machine", "reason", "at", "accepted", "refused"?}</c>; replicated.</summary>
    internal const string Bootstraps = "catalog_gate_bootstraps";

    private const int MaxReasonBytes = 500;
    private static readonly TimeSpan Pass = TimeSpan.FromSeconds(1);

    // The first pass runs before the listeners start; past this, the machine starts closed and the passes go on.
    private static readonly TimeSpan StartBudget = TimeSpan.FromSeconds(5);

    private readonly IMembership _membership;
    private readonly IMemberDirectory? _directory;
    private readonly AntiEntropy _antiEntropy;
    private readonly IReplicaStates _states;
    private readonly IStorageEngine _engine;
    private readonly TerraBaseOptions _options;
    private readonly LocalReplica _self;
    private readonly TimeProvider _time;
    private readonly ILogger<CatalogGate> _logger;
    private readonly HashSet<string> _verified = [with(StringComparer.Ordinal)];
    private readonly HashSet<string> _verifiedRegions = [with(StringComparer.Ordinal)];
    private readonly SemaphoreSlim _pass = new(1, 1);
    private volatile bool _open;

    public CatalogGate(
        IMembership membership, AntiEntropy antiEntropy, IReplicaStates states, IStorageEngine engine, TerraBaseOptions options, LocalReplica self,
        TimeProvider time, IMeterFactory meters, ILogger<CatalogGate> logger, IMemberDirectory? directory = null)
    {
        (_membership, _directory, _antiEntropy, _states, _engine, _options, _self, _time, _logger) =
            (membership, directory, antiEntropy, states, engine, options, self, time, logger);
        meters.Create(Telemetry.MeterName).CreateObservableGauge("terrabase.catalog.verified", () => _open ? 1 : 0,
            description: "1 once this machine's catalog was verified since it started (or bootstrapped as a new cluster's), else 0.");
    }

    public bool IsOpen => _open;

    /// <summary>What the gate still waits for, as of the last pass: machine ids and <c>region:&lt;region&gt;</c> entries.</summary>
    public ImmutableArray<string> Pending { get; private set; } = [];

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        // A machine of a cluster records itself; a single machine by configuration leaves its catalog uncreated until used.
        if (_directory is not null || await ListedAsync(cancellationToken).ConfigureAwait(false) is { Count: > 0 })
        {
            await RecordMemberAsync(cancellationToken).ConfigureAwait(false);
        }

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
            try
            {
                _ = await VerifyAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.ZLogWarning(error, $"A catalog gate pass failed; this machine stays closed and tries again");
            }
        }
    }

    /// <summary>One pass: pulls from every machine still needed; whether the gate is open after it.</summary>
    internal async Task<bool> VerifyAsync(CancellationToken cancellationToken)
    {
        await _pass.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_open)
            {
                return true;
            }

            if (await ListedAsync(cancellationToken).ConfigureAwait(false) is not { } listed)
            {
                Pending = ["member directory"];
                return false;
            }

            foreach (var peer in listed.Where(peer => !_verified.Contains(peer.Name)))
            {
                try
                {
                    _ = await _antiEntropy.PullAsync(ChangeGroups.System, peer, cancellationToken).ConfigureAwait(false);
                    _ = _verified.Add(peer.Name);
                    _ = _verifiedRegions.Add(peer.Region);
                }
                catch (Exception error) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.ZLogWarning(error, $"The catalog could not be pulled from machine {peer.Name:@Peer} in {peer.Region:@Region}; this machine stays closed and tries again");
                }
            }

            // Read after the pulls: a pull may record more regions. A catalog not created yet records none.
            var regions = _engine.SystemDatabaseExists ? await RecordedRegionsAsync(cancellationToken).ConfigureAwait(false) : [];
            Pending = [.. listed.Select(peer => peer.Name).Where(name => !_verified.Contains(name)),
                .. regions.Where(region => !_verifiedRegions.Contains(region)).Select(region => $"region:{region}")];
            if (Pending.IsEmpty && (_verified.Count > 0 || _directory is null))
            {
                // Pulled from every machine needed, at least one of them; or a single machine by configuration.
                _open = true;
                _logger.ZLogInformation($"The catalog is verified against {_verified.Count} other machines; this machine serves");
            }
            else
            {
                await TryBootstrapAsync(listed.Count + regions.Count + _verified.Count == 0, cancellationToken).ConfigureAwait(false);
            }

            return _open;
        }
        finally
        {
            _pass.Release();
        }
    }

    // Opens a new cluster's first machine on an operator's request, only when no other machine is listed, recorded or pulled
    // from (alone) and the catalog is genesis: it never joined a state of another machine and never held an organization.
    // Records every request, accepted or refused.
    private async Task TryBootstrapAsync(bool alone, CancellationToken cancellationToken)
    {
        var path = Path.Combine(_options.DataDirectory, BootstrapFile);
        if (!File.Exists(path))
        {
            return;
        }

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        File.Delete(path);
        var reason = bytes.Length is > 0 and <= MaxReasonBytes ? Encoding.UTF8.GetString(bytes).Trim() : string.Empty;
        var refused = reason.Length == 0 ? "the request holds no reason of 1 to 500 bytes"
            : !alone ? "another machine is listed or recorded: this machine opens by pulling from it"
            : await GenesisAsync(cancellationToken).ConfigureAwait(false) ? null
            : "the catalog is not a new cluster's: it holds another machine's state or an organization";
        await AuditAsync(reason, refused, cancellationToken).ConfigureAwait(false);
        if (refused is not null)
        {
            _logger.ZLogWarning($"A catalog bootstrap request was refused ({refused}); this machine stays closed");
            return;
        }

        _open = true;
        _logger.ZLogWarning($"The catalog was bootstrapped as a new cluster's on an operator's request: {reason:@Reason}");
    }

    // A genesis catalog: every version of every state is this machine's, and no organization was ever recorded.
    private async Task<bool> GenesisAsync(CancellationToken cancellationToken)
    {
        var buckets = Enumerable.Range(0, 4096).ToList();
        BucketCursor? after = null;
        do
        {
            (var states, after) = await _states.ReadBucketsAsync(ChangeGroups.System, buckets, after, 1024, ChangeGroups.MaxBatchBytes, cancellationToken)
                .ConfigureAwait(false);
            if (states.Any(state => state.Collection == "organizations"
                || state.State.Versions().Any(version => version.Node.Region != _self.Id.Region || version.Node.Member != _self.Id.Member)))
            {
                return false;
            }
        }
        while (after is not null);
        return true;
    }

    // A bootstrap request's record, in a collection of its own (a machines/ document would make a new catalog look like an
    // old format to the management catalog's first initialization).
    private async Task AuditAsync(string reason, string? refused, CancellationToken cancellationToken)
    {
        using var body = new MemoryStream();
        await using (var json = new Utf8JsonWriter(body))
        {
            json.WriteStartObject();
            json.WriteString("machine", _options.MachineId);
            json.WriteString("reason", reason);
            json.WriteString("at", _time.GetUtcNow());
            json.WriteBoolean("accepted", refused is null);
            if (refused is not null)
            {
                json.WriteString("refused", refused);
            }

            json.WriteEndObject();
        }

        await WriteAsync(Bootstraps, EntityIds.New().ToString(), body.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    // This machine's member record, so every machine knows its region even while it is stopped.
    private async Task RecordMemberAsync(CancellationToken cancellationToken)
    {
        var store = await _engine.OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false);
        await using (var transaction = await store.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false))
        {
            if (await transaction.GetAsync(Members, _options.MachineId, cancellationToken).ConfigureAwait(false) is { } known
                && RegionOf(known.Json.Span) == _options.Region)
            {
                return;
            }
        }

        await WriteAsync(Members, _options.MachineId, Encoding.UTF8.GetBytes($$"""{"region":{{JsonSerializer.Serialize(_options.Region, TerraBaseJsonContext.Default.String)}}}"""),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteAsync(string collection, string id, byte[] json, CancellationToken cancellationToken)
    {
        var store = await _engine.OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await store.BeginTransactionAsync(write: true, cancellationToken).ConfigureAwait(false);
        _ = await transaction.PutAsync(collection, id, json, cancellationToken).ConfigureAwait(false);
        _ = await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    // The regions of every other machine the catalog records, this machine's own region aside.
    private async Task<HashSet<string>> RecordedRegionsAsync(CancellationToken cancellationToken)
    {
        const int Page = 100;
        var regions = new HashSet<string>(StringComparer.Ordinal);
        var store = await _engine.OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await store.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false);
        string? afterId = null;
        do
        {
            var page = await transaction.ListAsync(Members, afterId, Page, 1024 * 1024, cancellationToken).ConfigureAwait(false);
            foreach (var member in page.Documents.Where(member => member.Id != _options.MachineId))
            {
                if (RegionOf(member.Json.Span) is { } region && region != _options.Region)
                {
                    _ = regions.Add(region);
                }
            }

            afterId = page.Documents.Count == Page ? page.Documents[^1].Id : null;
        }
        while (afterId is not null);
        return regions;
    }

    private static string? RegionOf(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json);
        return JsonDocument.TryParseValue(ref reader, out var document) && document.RootElement.TryGetProperty("region", out var region)
            && region.ValueKind == JsonValueKind.String ? region.GetString() : null;
    }

    // The other machines to pull from, or null while the list cannot be trusted (a failed directory read).
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
    /// answers 503 <c>catalog_unverified</c> before authentication. Probes, metrics, status and the peers' gRPC (which other
    /// machines' gates pull from) still run.
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
