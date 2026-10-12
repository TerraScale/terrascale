using System.Collections.Immutable;
using System.Diagnostics.Metrics;
using System.Text;
using System.Text.Json;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Engine.Cluster;
using TerraBase.Features.Backup;
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
/// at once) completed since this start from every other machine the member directory lists as started and every machine the
/// catalog records (<see cref="Members"/>), which covers a stopped machine that holds the only copy of a change; and only when
/// the catalog is not behind the head this machine anchored in its backup bucket (a restore of an older copy, which no pull
/// among restored peers can fix). Nothing to pull from is unknown, not proof of a current catalog: the gate stays closed.
/// Ways out without pulls: the cell is a single machine by configuration (no member directory, nothing listed or recorded),
/// an operator requests the bootstrap of a new cluster (<see cref="BootstrapFile"/>, genesis catalogs only), or an operator
/// opens it for one start (<see cref="OpenFile"/>, audited). Nothing else opens it, however long.
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

    /// <summary>
    /// The operator's open: a file of that name in the data directory, <c>&lt;operator&gt;: &lt;reason&gt;</c>, written through
    /// <c>fly ssh</c>. It opens a closed gate without the missing verifications, for this start only (a cluster down to one
    /// machine, a region gone for good); consumed by the next pass, accepted or not. Never written by TerraBase.
    /// </summary>
    internal const string OpenFile = "catalog-open";

    /// <summary>Every operator open a machine read: <c>{"machine", "operator", "reason", "at", "accepted", "refused"?, "unverified"}</c>.</summary>
    internal const string Opens = "catalog_gate_opens";

    private const int MaxReasonBytes = 500;
    private static readonly TimeSpan Pass = TimeSpan.FromSeconds(1);

    // How often an open machine moves its restore anchor to its catalog's head.
    private static readonly TimeSpan AnchorEvery = TimeSpan.FromSeconds(15);

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
    private readonly IChangeGroupSource _source;
    private readonly IObjectStore? _anchors;
    private readonly DataKeys? _keys;
    private readonly SemaphoreSlim _pass = new(1, 1);
    private volatile bool _open;

    // The catalog's change log head at this start, before any pull: what the restore anchor is compared with.
    private long _startHead;

    // Whether the anchor shows this catalog restored to an older copy: null until it was read.
    private bool? _restored;

    // The head last written to the anchor.
    private long _anchored = -1;

    /// <summary>The catalog head last written to the restore anchor, or -1.</summary>
    internal long Anchored => Interlocked.Read(ref _anchored);

    public CatalogGate(
        IMembership membership, AntiEntropy antiEntropy, IReplicaStates states, IChangeGroupSource source, IStorageEngine engine, TerraBaseOptions options,
        LocalReplica self, TimeProvider time, IMeterFactory meters, ILogger<CatalogGate> logger, IMemberDirectory? directory = null,
        IObjectStore? bucket = null, DataKeys? keys = null)
    {
        (_membership, _directory, _antiEntropy, _states, _source, _engine, _options, _self, _time, _logger) =
            (membership, directory, antiEntropy, states, source, engine, options, self, time, logger);
        // The anchor needs the backup bucket and its keys: it is sealed like every backup object.
        (_anchors, _keys) = bucket is not null && keys is not null ? (bucket, keys) : (null, null);
        meters.Create(Telemetry.MeterName).CreateObservableGauge("terrabase.catalog.verified", () => _open ? 1 : 0,
            description: "1 once this machine's catalog was verified since it started (or bootstrapped as a new cluster's), else 0.");
    }

    public bool IsOpen => _open;

    /// <summary>What the gate still waits for, as of the last pass: machine ids, and the restore anchor's verdict.</summary>
    public ImmutableArray<string> Pending { get; private set; } = [];

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        _startHead = _engine.SystemDatabaseExists ? await _source.HeadAsync(ChangeGroups.System, cancellationToken).ConfigureAwait(false) : 0;

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
        if (!_open)
        {
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

        if (_anchors is null)
        {
            return;
        }

        // Open: the anchor follows the catalog's head, so a later restore of an older copy shows.
        using var anchorTimer = new PeriodicTimer(AnchorEvery, _time);
        while (await anchorTimer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await WriteAnchorAsync(stoppingToken).ConfigureAwait(false);
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

            _restored ??= await ReadAnchorAsync(cancellationToken).ConfigureAwait(false);

            foreach (var peer in listed.Where(peer => !_verified.Contains(peer.Name)))
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

            // Read after the pulls: a pull may record more machines. A catalog not created yet records none.
            var recorded = _engine.SystemDatabaseExists ? await RecordedMachinesAsync(cancellationToken).ConfigureAwait(false) : [];
            Pending = [.. listed.Select(peer => peer.Name).Union(recorded, StringComparer.Ordinal).Where(name => !_verified.Contains(name)),
                .. _restored switch
                {
                    null => ["restore anchor unread"],
                    true => ["restored to an older copy: an operator's decision"],
                    false => Array.Empty<string>(),
                }];
            if (Pending.IsEmpty && (_verified.Count > 0 || _directory is null))
            {
                // Pulled from every machine needed, at least one of them, and not restored; or a single machine by configuration.
                _open = true;
                _logger.ZLogInformation($"The catalog is verified against {_verified.Count} other machines; this machine serves");
            }
            else
            {
                await TryBootstrapAsync(_restored == false && listed.Count + recorded.Count + _verified.Count == 0, cancellationToken).ConfigureAwait(false);
                if (!_open)
                {
                    await TryOperatorOpenAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            if (_open)
            {
                await WriteAnchorAsync(cancellationToken).ConfigureAwait(false);
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
        if (await ConsumeAsync(BootstrapFile, cancellationToken).ConfigureAwait(false) is not { } reason)
        {
            return;
        }

        var refused = reason.Length == 0 ? "the request holds no reason of 1 to 500 bytes"
            : !alone ? "another machine is listed or recorded (it opens by pulling from it), or the catalog is restored or its anchor unread"
            : await GenesisAsync(cancellationToken).ConfigureAwait(false) ? null
            : "the catalog is not a new cluster's: it holds another machine's state or an organization";
        await AuditAsync(Bootstraps, null, reason, refused, null, cancellationToken).ConfigureAwait(false);
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
    // Opens a closed gate on an operator's request naming who and why, whatever the catalog holds, and records it with what
    // was not verified. Not automatic: TerraBase never writes the file, and it is consumed.
    private async Task TryOperatorOpenAsync(CancellationToken cancellationToken)
    {
        if (await ConsumeAsync(OpenFile, cancellationToken).ConfigureAwait(false) is not { } request)
        {
            return;
        }

        var separator = request.IndexOf(':', StringComparison.Ordinal);
        var (who, reason) = separator > 0 ? (request[..separator].Trim(), request[(separator + 1)..].Trim()) : (string.Empty, string.Empty);
        var refused = who.Length == 0 || reason.Length == 0 ? "the request is not \"<operator>: <reason>\" within 500 bytes" : null;
        await AuditAsync(Opens, who, reason, refused, Pending, cancellationToken).ConfigureAwait(false);
        if (refused is not null)
        {
            _logger.ZLogWarning($"An operator open of the catalog gate was refused ({refused}); this machine stays closed");
            return;
        }

        _open = true;
        _logger.ZLogWarning($"Operator {who:@Operator} opened the catalog gate without verifying [{string.Join(", ", Pending):@Unverified}] for this start: {reason:@Reason}");
    }

    // An operator request file's text, trimmed (empty when it is empty or over 500 bytes), deleted once read; null when absent.
    private async Task<string?> ConsumeAsync(string file, CancellationToken cancellationToken)
    {
        var path = Path.Combine(_options.DataDirectory, file);
        if (!File.Exists(path))
        {
            return null;
        }

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        File.Delete(path);
        return bytes.Length is > 0 and <= MaxReasonBytes ? Encoding.UTF8.GetString(bytes).Trim() : string.Empty;
    }

    private async Task AuditAsync(
        string collection, string? who, string reason, string? refused, IReadOnlyList<string>? unverified, CancellationToken cancellationToken)
    {
        using var body = new MemoryStream();
        await using (var json = new Utf8JsonWriter(body))
        {
            json.WriteStartObject();
            json.WriteString("machine", _options.MachineId);
            if (who is not null)
            {
                json.WriteString("operator", who);
            }

            json.WriteString("reason", reason);
            json.WriteString("at", _time.GetUtcNow());
            json.WriteBoolean("accepted", refused is null);
            if (refused is not null)
            {
                json.WriteString("refused", refused);
            }

            if (unverified is not null)
            {
                json.WriteStartArray("unverified");
                foreach (var pending in unverified)
                {
                    json.WriteStringValue(pending);
                }

                json.WriteEndArray();
            }

            json.WriteEndObject();
        }

        await WriteAsync(collection, EntityIds.New().ToString(), body.ToArray(), cancellationToken).ConfigureAwait(false);
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

    // Every other machine the catalog records.
    private async Task<HashSet<string>> RecordedMachinesAsync(CancellationToken cancellationToken)
    {
        const int Page = 100;
        var machines = new HashSet<string>(StringComparer.Ordinal);
        var store = await _engine.OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await store.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false);
        string? afterId = null;
        do
        {
            var page = await transaction.ListAsync(Members, afterId, Page, 1024 * 1024, cancellationToken).ConfigureAwait(false);
            machines.UnionWith(page.Documents.Select(member => member.Id).Where(id => id != _options.MachineId));
            afterId = page.Documents.Count == Page ? page.Documents[^1].Id : null;
        }
        while (afterId is not null);
        return machines;
    }

    // Whether the catalog is behind the head this machine last anchored outside its volume (a restored older copy), or null
    // while that cannot be told: the anchor unreadable, or no backup bucket on Fly. Off Fly without a bucket (dev servers,
    // tests) nothing is anchored and nothing is detected.
    private async Task<bool?> ReadAnchorAsync(CancellationToken cancellationToken)
    {
        if (_anchors is null)
        {
            if (_options.App is null)
            {
                return false;
            }

            _logger.ZLogWarning($"No backup bucket holds this machine's catalog anchor: a restore cannot be told apart, so the catalog gate stays closed");
            return null;
        }

        try
        {
            long? anchored = null;
            _ = await _anchors.ReadAsync(AnchorKey, async (stream, token) =>
            {
                // Authenticated: an anchor edited or forged without the key fails here, and the gate stays closed.
                using var plain = new MemoryStream();
                await BackupObject.ReadAsync(stream, plain, AnchorKey, id => _keys!.GetAsync(KeyScope, id, token), token).ConfigureAwait(false);
                using var document = JsonDocument.Parse(plain.ToArray());
                anchored = document.RootElement.GetProperty("head").GetInt64();
            }, cancellationToken).ConfigureAwait(false);
            if (anchored is { } head && head > _startHead)
            {
                _logger.ZLogWarning($"This machine's catalog (head {_startHead}) is behind its anchor (head {head}): it was restored to an older copy; the catalog gate stays closed until an operator decides");
                return true;
            }

            return false;
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.ZLogWarning(error, $"This machine's catalog anchor could not be read; the catalog gate stays closed and tries again");
            return null;
        }
    }

    // Records the catalog's head outside the volume, when it moved. A failure leaves the last anchor; the next write catches up.
    private async Task WriteAnchorAsync(CancellationToken cancellationToken)
    {
        if (_anchors is null)
        {
            return;
        }

        try
        {
            var head = await _source.HeadAsync(ChangeGroups.System, cancellationToken).ConfigureAwait(false);
            if (head == _anchored)
            {
                return;
            }

            using var body = new MemoryStream();
            await using (var json = new Utf8JsonWriter(body))
            {
                json.WriteStartObject();
                json.WriteNumber("head", head);
                json.WriteString("at", _time.GetUtcNow());
                json.WriteEndObject();
            }

            body.Position = 0;
            var (keyId, dataKey) = await _keys!.CurrentAsync(KeyScope, cancellationToken).ConfigureAwait(false);
            using var sealedAnchor = new MemoryStream();
            await BackupObject.WriteAsync(body, sealedAnchor, AnchorKey, keyId, dataKey, cancellationToken).ConfigureAwait(false);
            sealedAnchor.Position = 0;
            await _anchors.PutAsync(AnchorKey, sealedAnchor, cancellationToken).ConfigureAwait(false);
            Interlocked.Exchange(ref _anchored, head);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.ZLogWarning(error, $"This machine's catalog anchor could not be written; it is tried again");
        }
    }

    private string AnchorKey => $"catalog-anchor/{_options.MachineId}";

    private static string KeyScope => BackupLayout.KeyScope(null);

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
