using System.Collections.Immutable;
using System.Diagnostics.Metrics;
using System.Text;
using System.Text.Json;
using Grpc.Core;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Engine.Cluster;
using TerraBase.Errors;
using TerraBase.Features.Backup;
using TerraBase.Hosting;
using ZLogger;

namespace TerraBase.Features.Replication;

/// <summary>
/// Whether this machine's catalog may be trusted for serving (docs/terrabase/restore-drill.md): a
/// catalog restored from an older copy reads removed entries live (revoked keys, removed members, deleted ids) until it is
/// repaired, and nothing on this machine can tell a restored file from a current one. So every start is unverified until
/// the catalog has been pulled from the other machines.
/// </summary>
public interface ICatalogGate
{
    /// <summary>Whether the catalog is verified (or bootstrapped as a new cluster's): requests that read it for authorization run.</summary>
    bool IsOpen { get; }

    /// <summary>Completes once <see cref="IsOpen"/> is true; an open gate never closes again.</summary>
    Task Opened { get; }

    /// <summary>
    /// Whether this catalog is known not to be a restored older copy: open, or its restore anchor read and not ahead of it.
    /// Another machine's gate counts a pull from this one only then (a closed, restored machine verifies nobody).
    /// </summary>
    bool Trusted { get; }

    /// <summary>What the gate still waits for, as of its last pass: machine ids and reasons (see <see cref="CatalogGate.Pending"/>).</summary>
    IReadOnlyList<string> Waiting { get; }

    /// <summary>
    /// A digest of this machine's whole catalog (SHA-256 over its 4096 anti-entropy bucket digests, hex), or null before the
    /// catalog exists: machines whose catalogs converged report the same value. Evidence for the operator, never a credential.
    /// </summary>
    Task<string?> DigestAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Records the catalog's head in the restore anchor now (when it moved and the machine has a backup bucket), so a change
    /// about to be acknowledged cannot be lost to a restore unnoticed. False when the anchor could not be written within
    /// <see cref="CatalogGate.AnchorTimeout"/>: the change is committed here and replicates, but is not acknowledged
    /// (<see cref="CatalogGate.AnchorUnavailable"/>).
    /// </summary>
    Task<bool> TryAnchorAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Endpoint metadata: a request a restore could undo to a tenant's harm (a revocation, a removal, a deletion of catalog
/// state). Its success is anchored before it is acknowledged (<see cref="ICatalogGate.TryAnchorAsync"/>); decided on the routed
/// endpoint, so a rewritten or aliased path cannot skip it. Tenant data routes never carry it.
/// </summary>
public sealed class AnchoredRevocation
{
    private AnchoredRevocation()
    {
    }

    /// <summary>The marker.</summary>
    public static AnchoredRevocation Instance { get; } = new();
}

/// <summary>
/// The restore gate. Closed at every start. Opens once a forced anti-entropy pull of <c>_system</c> (every differing bucket,
/// at once) completed since this start from every other machine the member directory lists as started and every machine the
/// catalog records (<see cref="Members"/>), which covers a stopped machine that holds the only copy of a change; and only when
/// the catalog is not behind the head this machine anchored in its backup bucket (a restore of an older copy, which no pull
/// among restored peers can fix). Nothing to pull from is unknown, not proof of a current catalog: the gate stays closed.
/// A new empty catalog starts automatically. An existing catalog behind its anchor stays closed for reviewed recovery;
/// there is no operator bypass.
/// </summary>
internal sealed partial class CatalogGate : BackgroundService, ICatalogGate
{
    /// <summary>Every machine that ever ran this cluster's catalog: id = machine id, body = <c>{"region": …}</c>; replicated.</summary>
    internal const string Members = "cluster_members";

    private const int MaxMembers = 1024;
    private static readonly TimeSpan Pass = TimeSpan.FromSeconds(1);

    // How often an open machine moves its restore anchor to its catalog's head.
    private static readonly TimeSpan AnchorEvery = TimeSpan.FromSeconds(15);

    /// <summary>The longest an anchor write may take, waiting for another one included: a revocation answers within it.</summary>
    internal static readonly TimeSpan AnchorTimeout = TimeSpan.FromSeconds(10);

    /// <summary>A revocation whose anchor could not be written in time: committed here and replicating, not acknowledged.</summary>
    internal static readonly Unavailable AnchorUnavailable = new(
        "the change is committed on this machine and replicates, but its restore anchor could not be written in time; retry", "catalog_anchor_unavailable");

    // The first pass runs before the listeners start; past this, the machine starts closed and the passes go on.
    private static readonly TimeSpan StartBudget = TimeSpan.FromSeconds(5);

    private readonly IMembership _membership;
    private readonly PeerCredential _credential;
    private readonly IMemberDirectory? _directory;
    private readonly AntiEntropy _antiEntropy;
    private readonly IReplicaStates _states;
    private readonly IStorageEngine _engine;
    private readonly TerraBaseOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<CatalogGate> _logger;
    private readonly HashSet<string> _verified = [with(StringComparer.Ordinal)];
    private readonly IChangeGroupSource _source;
    private readonly IObjectStore? _anchors;
    private readonly DataKeys? _keys;
    private readonly SemaphoreSlim _pass = new(1, 1);
    private readonly SemaphoreSlim _anchoring = new(1, 1);
    private volatile bool _open;
    private readonly TaskCompletionSource _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // The catalog's change log head at this start, before any pull: what the restore anchor is compared with.
    private long _startHead;
    private Ulid _startIncarnation;
    private bool _fresh;

    // Whether the anchor shows this catalog restored to an older copy: null until it was read.
    private bool? _restored;

    // No anchor for a catalog that is not genesis (another machine's state, an organization, other recorded machines): it may
    // be a restore whose anchor went missing, so it opens only by verified pulls from trusted peers, after reviewed recovery.
    private volatile bool _anchorMissing;

    // Whether this machine's anchor was read and its catalog is not behind it: known not restored, so it may open from a
    // majority of trusted machines (itself included) rather than every one.
    private bool _anchorHeld;

    // The head last written to the anchor.
    private long _anchored = -1;
    private long _anchorTimeouts;

    /// <summary>The catalog head last written to the restore anchor, or -1.</summary>
    internal long Anchored => Interlocked.Read(ref _anchored);

    public CatalogGate(
        IMembership membership, AntiEntropy antiEntropy, IReplicaStates states, IChangeGroupSource source, IStorageEngine engine, TerraBaseOptions options,
        TimeProvider time, IMeterFactory meters, ILogger<CatalogGate> logger, PeerCredential credential, LocalReplica self, IServiceProvider services, IMemberDirectory? directory = null,
        IObjectStore? bucket = null, DataKeys? keys = null)
    {
        _credential = credential;
        (_self, _services) = (self, services);
        (_membership, _directory, _antiEntropy, _states, _source, _engine, _options, _time, _logger) =
            (membership, directory, antiEntropy, states, source, engine, options, time, logger);
        // The anchor needs the backup bucket and its keys: it is sealed like every backup object.
        (_anchors, _keys) = bucket is not null && keys is not null ? (bucket, keys) : (null, null);
        meters.Create(Telemetry.MeterName).CreateObservableCounter("terrabase.catalog.anchor.timeouts",
            () => Interlocked.Read(ref _anchorTimeouts),
            description: "Catalog anchor attempts cancelled by their own ten-second deadline, including waiting for the anchor writer; excludes caller cancellation.");
        meters.Create(Telemetry.MeterName).CreateObservableGauge("terrabase.catalog.verified", () => _open ? 1 : 0,
            description: "1 once this machine's catalog was verified or created empty, else 0.");
    }

    public bool IsOpen => _open;

    public Task Opened => _opened.Task;

    public bool Trusted => _open || (_restored == false && !_anchorMissing);

    public IReadOnlyList<string> Waiting => _revocationStage is { } stage ? Pending.Add(stage) : Pending;

    public async Task<string?> DigestAsync(CancellationToken cancellationToken) =>
        _engine.SystemDatabaseExists && await _states.DigestAsync(ChangeGroups.System, expected: null, cancellationToken).ConfigureAwait(false) is { } digest
            ? Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(digest.Buckets))
            : null;

    /// <summary>What the gate still waits for, as of the last pass: machine ids, and the restore anchor's verdict.</summary>
    public ImmutableArray<string> Pending { get; private set; } = ["initial catalog verification"];

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        _fresh = !_engine.SystemDatabaseExists;
        // The head is read pinned to the identity found here: a head of another copy never comes back.
        if (_fresh)
        {
            (_startHead, _startIncarnation) = (0, Ulid.Empty);
        }
        else
        {
            var incarnation = (await _engine.OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false)).Incarnation;
            if (await _source.HeadAsync(ChangeGroups.System, incarnation, cancellationToken).ConfigureAwait(false) is not { } head)
            {
                throw new InvalidOperationException("the catalog exists but its head could not be read; the catalog gate stays closed");
            }

            (_startHead, _startIncarnation) = (head, incarnation);
        }

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
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            // Out of start budget (a pull it cancels throws RpcException, not OperationCanceledException) or a failed pass:
            // the host starts either way, closed until a later pass verifies it.
            if (!budget.IsCancellationRequested)
            {
                _logger.ZLogWarning(error, $"The catalog gate's first pass failed; this machine starts closed and tries again");
            }
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
                catch (RpcException error) when (stoppingToken.IsCancellationRequested && error.StatusCode == StatusCode.Cancelled)
                {
                    stoppingToken.ThrowIfCancellationRequested();
                    throw;
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
            // A newly created catalog cannot revive credentials. Existing catalogs never use this path.
            if (_fresh && listed.Count == 0 && _restored == false && !_anchorMissing)
            {
                _open = true;
                _opened.TrySetResult();
                Pending = [];
                await WriteAnchorAsync(cancellationToken).ConfigureAwait(false);
                return true;
            }

            // Every machine still needed at once: each pull is a WAN round trip and a bucket repair, so a pass waits for the
            // slowest machine instead of their sum (TS-983: 23-60 s to open after a restart, racing readiness).
            var pulls = await Task.WhenAll(listed.Where(peer => !_verified.Contains(peer.Name))
                .Select(peer => VerifyPeerAsync(peer, cancellationToken))).ConfigureAwait(false);
            foreach (var peer in pulls.OfType<string>())
            {
                if (_verified.Count == MaxMembers && !_verified.Contains(peer))
                {
                    throw new InvalidDataException("the catalog gate's verified member limit was exceeded");
                }

                _ = _verified.Add(peer);
            }

            // Read after the pulls: a pull may record more machines. A catalog not created yet records none.
            var recorded = _engine.SystemDatabaseExists ? await RecordedMachinesAsync(cancellationToken).ConfigureAwait(false) : [];
            Pending = [.. listed.Select(peer => peer.Name).Union(recorded, StringComparer.Ordinal).Where(name => !_verified.Contains(name)),
                .. _restored switch
                {
                    null => ["restore anchor unread"],
                    true => ["restored to an older copy: reviewed recovery required"],
                    false when _anchorMissing && _verified.Count == 0 => ["restore anchor missing: a trusted peer after reviewed recovery"],
                    false => Array.Empty<string>(),
                }];
            // Known not restored (its anchor holds): pulls from trusted machines that make a majority with this one are enough,
            // so one region's hard stop never keeps another region's restarted machine closed. Without that proof, every one.
            var machines = listed.Select(peer => peer.Name).Union(recorded, StringComparer.Ordinal).Count(name => name != _options.MachineId) + 1;
            var majority = _anchorHeld && _restored == false && (_verified.Count + 1) * 2 > machines;
            // A fresh file cannot revive old security state; destroyed historical members need not vouch for it.
            var freshPull = _fresh && _restored == false && listed.Count > 0 && listed.All(peer => _verified.Contains(peer.Name));
            var revocationsHeld = await RevocationsHeldAsync(listed, cancellationToken).ConfigureAwait(false);
            if (revocationsHeld && ((Pending.IsEmpty && (_verified.Count > 0 || (_directory is null && !_anchorMissing))) || majority || freshPull))
            {
                // Pulled from every machine needed (or a majority, see above), at least one of them, and not restored; or a
                // single machine by configuration.
                _open = true;
                _opened.TrySetResult();
                _logger.ZLogInformation($"The catalog is verified against {_verified.Count} of {machines - 1} other machines; this machine serves");
            }

            if (_open)
            {
                // It waits for nothing now; nothing authorizes a bypass.
                Pending = [];
                await WriteAnchorAsync(cancellationToken).ConfigureAwait(false);
            }

            return _open;
        }
        finally
        {
            _pass.Release();
        }
    }

    // Only catalog metadata and membership may exist; retained deletions count as application state. Every page reads the
    // copy the gate started on (a catalog created since then: the one in place at the first page); any other fails closed.
    private async Task<bool> GenesisAsync(CancellationToken cancellationToken)
    {
        Ulid incarnation;
        if (_startIncarnation != Ulid.Empty)
        {
            incarnation = _startIncarnation;
        }
        else if (!_engine.SystemDatabaseExists)
        {
            // No catalog at all holds nothing but genesis.
            return true;
        }
        else
        {
            incarnation = (await _engine.OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false)).Incarnation;
        }

        var buckets = Enumerable.Range(0, 4096).ToList();
        BucketCursor? after = null;
        do
        {
            if (await _states.ReadBucketsAsync(ChangeGroups.System, incarnation, buckets, after, 1024, ChangeGroups.MaxBatchBytes, cancellationToken)
                .ConfigureAwait(false) is not (var states, var next))
            {
                return false;
            }

            after = next;
            if (states.Any(state => state.Collection is not ("catalog" or Members)))
            {
                return false;
            }
        }
        while (after is not null);
        return await _source.HeadAsync(ChangeGroups.System, incarnation, cancellationToken).ConfigureAwait(false) is not null;
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

    // Whether the peer's catalog is known not to be a restored older copy (its gate's Trusted, GET /catalog-gate on its
    // internal listener); a pull from it verifies nothing otherwise.
    // Pulls the catalog from one trusted machine; its name once the pull holds what it had, null otherwise.
    private async Task<string?> VerifyPeerAsync(ClusterMember peer, CancellationToken cancellationToken)
    {
        var started = _time.GetTimestamp();
        try
        {
            if (!await PeerTrustedAsync(peer, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            var pulled = await _antiEntropy.PullAsync(ChangeGroups.System, peer, cancellationToken).ConfigureAwait(false);
            var trusted = pulled is not null && await PeerTrustedAsync(peer, cancellationToken).ConfigureAwait(false);
            _logger.ZLogInformation($"The catalog pull from machine {peer.Name:@Peer} in {peer.Region:@Region} took {_time.GetElapsedTime(started).TotalMilliseconds:F0} ms and repaired {pulled ?? 0} states (verified: {trusted})");
            return trusted ? peer.Name : null;
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.ZLogWarning(error, $"The catalog could not be pulled from machine {peer.Name:@Peer} in {peer.Region:@Region} after {_time.GetElapsedTime(started).TotalMilliseconds:F0} ms; this machine stays closed and tries again");
            return null;
        }
    }

    private async Task<bool> PeerTrustedAsync(ClusterMember peer, CancellationToken cancellationToken)
    {
        using var client = new HttpClient(_credential.Wrap(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }))
        {
            BaseAddress = new Uri(peer.PeerAddress),
            DefaultRequestVersion = System.Net.HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Timeout = TimeSpan.FromSeconds(10),
        };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        cancellationToken = deadline.Token;
        using var response = await client.GetAsync(new Uri(CatalogGateStatusEndpoint.Path, UriKind.Relative), HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var body = new byte[64 * 1024 + 1];
        var length = await stream.ReadAtLeastAsync(body, body.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
        if (length == body.Length)
        {
            return false;
        }

        using var document = JsonDocument.Parse(body.AsMemory(0, length));
        return document.RootElement.TryGetProperty("trusted", out var trusted) && trusted.ValueKind == JsonValueKind.True;
    }

    // Every other machine the catalog records.
    private async Task<HashSet<string>> RecordedMachinesAsync(CancellationToken cancellationToken) =>
        new((await RecordedAsync(_engine, cancellationToken).ConfigureAwait(false)).Keys.Where(id => id != _options.MachineId), StringComparer.Ordinal);

    /// <summary>Every machine the catalog records (this one included), with its recorded region, or null when unreadable.</summary>
    internal static async Task<Dictionary<string, string?>> RecordedAsync(IStorageEngine engine, CancellationToken cancellationToken)
    {
        const int Page = 100;
        var machines = new Dictionary<string, string?>(StringComparer.Ordinal);
        var store = await engine.OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await store.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false);
        string? afterId = null;
        do
        {
            var page = await transaction.ListAsync(Members, afterId, Page, 1024 * 1024, cancellationToken).ConfigureAwait(false);
            foreach (var member in page.Documents)
            {
                machines[member.Id] = RegionOf(member.Json.Span);
            }

            // One more than the limit: the record of the machine reading them.
            if (machines.Count > MaxMembers + 1)
            {
                throw new InvalidDataException("the catalog gate's member limit was exceeded");
            }
            afterId = page.Documents.Count == Page ? page.Documents[^1].Id : null;
        }
        while (afterId is not null);
        return machines;
    }

    // Whether the catalog is behind the head this machine last anchored outside its volume (a restored older copy), or null
    // while that cannot be told: the anchor unreadable, or no backup bucket in a cluster. Outside one without a bucket (dev
    // servers, tests) nothing is anchored and nothing is detected.
    private async Task<bool?> ReadAnchorAsync(CancellationToken cancellationToken)
    {
        if (_anchors is null)
        {
            if (_options.PeersFile is null)
            {
                return false;
            }

            _logger.ZLogWarning($"No backup bucket holds this machine's catalog anchor: a restore cannot be told apart, so the catalog gate stays closed");
            return null;
        }

        try
        {
            long? anchored = null;
            string? incarnation = null;
            var found = await _anchors.ReadAsync(AnchorKey, async (stream, token) =>
            {
                // Authenticated: an anchor edited or forged without the key fails here, and the gate stays closed.
                using var plain = new AnchorBody();
                await BackupObject.ReadAsync(stream, plain, AnchorKey, id => _keys!.GetAsync(KeyScope, id, token), token).ConfigureAwait(false);
                using var document = JsonDocument.Parse(plain.ToArray());
                if (document.RootElement.GetProperty("version").GetInt32() != 1)
                {
                    throw new InvalidDataException("the catalog anchor has an unsupported format");
                }

                anchored = document.RootElement.GetProperty("head").GetInt64();
                incarnation = document.RootElement.GetProperty("incarnation").GetString();
                _revocation = document.RootElement.TryGetProperty("revocations", out _) ? ReadRevocation(document.RootElement) : null;
            }, cancellationToken).ConfigureAwait(false);
            if (!found)
            {
                // No anchor yet is fine only for a new cluster's catalog; any other may be a restore whose anchor went missing.
                _anchorMissing = !await GenesisAsync(cancellationToken).ConfigureAwait(false)
                    || (_engine.SystemDatabaseExists && (await RecordedMachinesAsync(cancellationToken).ConfigureAwait(false)).Count > 0);
                if (_anchorMissing)
                {
                    _logger.ZLogWarning($"This machine's catalog anchor is missing and its catalog is not a new cluster's: the catalog gate opens only by pulls from trusted peers after reviewed recovery");
                }

                return false;
            }

            if (!Ulid.TryParse(incarnation, out var identity) || identity != _startIncarnation
                || anchored is { } head && head > _startHead)
            {
                _logger.ZLogWarning($"This machine's catalog has a different file identity or is behind its anchor (local head {_startHead}): it was restored to an older copy; the catalog gate stays closed for reviewed recovery");
                return true;
            }

            if (_revocation is { } witness && witness.Replica != _self.Id)
            {
                // The authenticated anchor names this exact file and a commit no later than the head held at start.
                // That proves this particular prefix even when an unclean restart changed our local replica epoch.
                // Persist it in the same file's applied vector so a whole pinned snapshot can export the proof. A
                // marker joined on its own, a newer epoch, or a different/older file never reaches this branch.
                var store = await _engine.OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false);
                await using var transaction = await store.BeginDurableTransactionAsync(cancellationToken).ConfigureAwait(false);
                if (await transaction.GetIncarnationAsync(cancellationToken).ConfigureAwait(false) != _startIncarnation)
                {
                    // The proof names the startup file, not its replacement. Classify it as requiring reviewed
                    // recovery instead of retrying the same readable anchor forever as though it were unreadable.
                    _logger.ZLogWarning($"This machine's catalog changed while checking its anchored revocation cut; the catalog gate stays closed for reviewed recovery");
                    return true;
                }

                await transaction.RecordAppliedAsync(witness.Replica, witness.Stamp, cancellationToken).ConfigureAwait(false);
                _ = await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            _anchorHeld = true;
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
        try
        {
            await AnchorAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.ZLogWarning(error, $"This machine's catalog anchor could not be written; it is tried again");
        }
    }

    public async Task<bool> TryAnchorAsync(CancellationToken cancellationToken)
    {
        try
        {
            await AnchorAsync(cancellationToken, revocation: true).ConfigureAwait(false);
            return true;
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.ZLogWarning(error, $"This machine's catalog anchor could not be written within {AnchorTimeout.TotalSeconds}s; a revocation is not acknowledged");
            return false;
        }
    }

    // Throws when the anchor cannot be written within AnchorTimeout.
    private async Task AnchorAsync(CancellationToken cancellationToken, bool revocation = false)
    {
        if (_anchors is null)
        {
            return;
        }

        var callerCancellation = cancellationToken;
        using var deadline = new CancellationTokenSource(AnchorTimeout, _time);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        cancellationToken = bounded.Token;

        // One writer at a time, and the head read inside: the anchor never moves back. A change waits for the writer in flight
        // (the periodic anchor or another change's) within the deadline, then finds its head already anchored or writes it.
        var acquired = false;
        try
        {
            await _anchoring.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;
            if (revocation || _revocation is null)
            {
                _revocation = await MarkRevocationAsync(cancellationToken).ConfigureAwait(false);
            }

            // The head is read pinned to the identity it is anchored with: one read transaction checks both.
            var incarnation = (await _engine.OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false)).Incarnation;
            if (await _source.HeadAsync(ChangeGroups.System, incarnation, cancellationToken).ConfigureAwait(false) is not { } head)
            {
                throw new IOException("the catalog's head could not be read for its identity; the anchor is not written");
            }

            // A healthy rewrite or a rebuild swap renews the identity and may restart the change log lower: anchored too, or
            // the next start reads the new file as a restored older copy and stays closed.
            if (head <= Anchored && incarnation == _anchoredIncarnation)
            {
                return;
            }

            using var body = new MemoryStream();
            await using (var json = new Utf8JsonWriter(body))
            {
                json.WriteStartObject();
                json.WriteNumber("version", 1);
                json.WriteNumber("revocations", 1);
                json.WriteNumber("head", head);
                json.WriteString("incarnation", incarnation.ToString());
                json.WriteString("at", _time.GetUtcNow());
                if (_revocation is { } witness)
                {
                    json.WriteNumber("replica", witness.Replica.Value);
                    json.WriteNumber("stamp", witness.Stamp.Value);
                }
                json.WriteEndObject();
            }

            body.Position = 0;
            var (keyId, dataKey) = await _keys!.CurrentAsync(KeyScope, cancellationToken).ConfigureAwait(false);
            using var sealedAnchor = new MemoryStream();
            await BackupObject.WriteAsync(body, sealedAnchor, AnchorKey, keyId, dataKey, cancellationToken).ConfigureAwait(false);
            sealedAnchor.Position = 0;
            await _anchors.PutAsync(AnchorKey, sealedAnchor, cancellationToken).ConfigureAwait(false);
            _anchoredIncarnation = incarnation;
            Interlocked.Exchange(ref _anchored, head);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !callerCancellation.IsCancellationRequested)
        {
            Interlocked.Increment(ref _anchorTimeouts);
            throw;
        }
        finally
        {
            if (acquired)
            {
                _anchoring.Release();
            }
        }
    }

    private string AnchorKey => $"catalog-anchor/{_options.MachineId}";

    private static string KeyScope => BackupLayout.KeyScope(null);

    private static string? RegionOf(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json);
        if (!JsonDocument.TryParseValue(ref reader, out var document))
        {
            return null;
        }

        using (document)
        {
            return document.RootElement.TryGetProperty("region", out var region) && region.ValueKind == JsonValueKind.String ? region.GetString() : null;
        }
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

        // An existing file's anchor proves no rollback, not that discovery which omitted this machine is complete.
        if (members.Length > MaxMembers || (!_fresh && !members.Any(member => member.Name == _membership.Self.Name)))
        {
            return null;
        }

        return [.. members.Where(member => member.Name != _membership.Self.Name && member.PeerAddress.Length > 0)];
    }

    private sealed class AnchorBody : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position + buffer.Length > 4096)
            {
                throw new InvalidDataException("the catalog anchor exceeds its byte limit");
            }

            return base.WriteAsync(buffer, cancellationToken);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        // A rolling restart may follow a change the periodic anchor has not caught yet (a renewed identity): anchor it last,
        // so the next start opens on the catalog it left. Only an open gate anchors: a closed one may hold a restored copy.
        if (_open)
        {
            try
            {
                await AnchorAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                // Out of the stop budget (the host cancels it) or a failed put: the last periodic anchor stands, at most
                // AnchorEvery old. Faulting here ended the process with an unhandled exception (2026-10-28, gru and fra).
                _logger.ZLogWarning(error, $"This machine's catalog anchor was not written at stop; the last periodic anchor stands");
            }
        }
    }

    public override void Dispose()
    {
        _pass.Dispose();
        _anchoring.Dispose();
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
        app.Use(async (context, next) =>
        {
            if (!gate.IsOpen && context.GetEndpoint() is { } endpoint
                && endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAuthorizeData>() is not null
                && endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAllowAnonymous>() is null)
            {
                await Closed(context).ConfigureAwait(false);
                return;
            }

            if (!gate.IsOpen || context.GetEndpoint()?.Metadata.GetMetadata<AnchoredRevocation>() is null)
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            // A successful revocation, removal or deletion is anchored before it is acknowledged: its (small) response is held
            // until the anchor is written, and replaced by 503 catalog_anchor_unavailable when it cannot be in time
            // (creations and tenant writes do not wait for the bucket).
            var body = context.Response.Body;
            using var held = new MemoryStream();
            context.Response.Body = held;
            try
            {
                await next(context).ConfigureAwait(false);
            }
            finally
            {
                context.Response.Body = body;
            }

            if (context.Response.StatusCode < StatusCodes.Status400BadRequest && !await gate.TryAnchorAsync(context.RequestAborted).ConfigureAwait(false))
            {
                context.Response.Clear();
                await BadRequests.WriteAsync(context, StatusCodes.Status503ServiceUnavailable, CatalogGate.AnchorUnavailable.Code,
                    CatalogGate.AnchorUnavailable.Message, op: null).ConfigureAwait(false);
                return;
            }

            held.Position = 0;
            await held.CopyToAsync(body, context.RequestAborted).ConfigureAwait(false);
        });
        return app;
    }

    public static Task Closed(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        return BadRequests.WriteAsync(context, StatusCodes.Status503ServiceUnavailable, "catalog_unverified",
            "this machine's catalog is not verified against the other machines yet", op: null);
    }
}
