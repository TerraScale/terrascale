using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TerraBase.Configuration;
using TerraBase.Engine.Cluster;
using TerraBase.Features.Replication;
using TerraBase.Hosting;

namespace TerraBase.Features.Health;

/// <summary>An admitted or runtime peer identity; disconnected peers remain members.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TopologyPeer(string Node, string Region, string Address);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record TopologyManifest(
    [property: JsonRequired] long Generation, [property: JsonRequired] DateTimeOffset ObservedAt,
    [property: JsonRequired] string[] Regions, [property: JsonRequired] TopologyPeer[] Peers);

// Captured from the address actually passed to Kestrel, never re-read from discovery.
internal sealed record BoundPeerAddress(IPAddress Address);

/// <summary>Local comparison against ts-orch's independently admitted topology; never repairs membership.</summary>
public interface IPeerTopology
{
    /// <summary>Whether fresh, complete evidence agrees with the directory and loaded membership now.</summary>
    bool IsCurrent { get; }

    /// <summary>Refreshes the bounded comparison and returns its diagnostic without tenant information.</summary>
    ValueTask<TopologyStatus> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>Internal topology verdict. Age is -1 and generation is zero before a valid manifest is read.</summary>
public sealed record TopologyStatus(int Mismatch, double AgeSeconds, long Generation, string Reason, IReadOnlyList<TopologyPeer> Peers);

internal sealed class PeerTopology : BackgroundService, IPeerTopology
{
    private const int MaxBytes = 64 * 1024, MaxPeers = 64;
    private readonly TerraBaseOptions _options;
    private readonly IMembership _membership;
    private readonly IServiceProvider _services;
    private readonly TimeProvider _time;
    private readonly BoundPeerAddress _bound;
    private readonly SemaphoreSlim _reading = new(1, 1);
    private Task<Evidence>? _pending;
    private Evidence _evidence = new(null, 0, "unknown", []);

    private sealed record Evidence(DateTimeOffset? At, long Generation, string Reason, TopologyPeer[] Peers);

    public PeerTopology(TerraBaseOptions options, IMembership membership, IServiceProvider services, TimeProvider time, IMeterFactory meters, BoundPeerAddress bound)
    {
        (_options, _membership, _services, _time) = (options, membership, services, time);
        _bound = bound;
        var meter = meters.Create(Telemetry.MeterName);
        meter.CreateObservableGauge("terrabase_peer_topology_mismatch", () => Status().Mismatch,
            description: "One when admitted topology is unknown, stale or differs from the file or runtime membership.");
        meter.CreateObservableGauge("terrabase_peer_topology_generation_age_seconds", () => Status().AgeSeconds, "s",
            "Age of the admitted topology observation; -1 when unknown.");
    }

    public bool IsCurrent => Status().Mismatch == 0;

    private TopologyStatus Status()
    {
        var evidence = Volatile.Read(ref _evidence);
        if (_options.PeersFile is null)
        {
            return new(0, -1, 0, "local", []);
        }

        var age = evidence.At is { } at ? (_time.GetUtcNow() - at).TotalSeconds : -1;
        var reason = evidence.Reason == "ok" && (age > 30 || age < -5) ? "stale" : evidence.Reason;
        return new(reason == "ok" ? 0 : 1, age, evidence.Generation, reason, evidence.Peers);
    }

    public async ValueTask<TopologyStatus> ReadAsync(CancellationToken cancellationToken)
    {
        if (_options.PeersFile is null)
        {
            return Status();
        }

        if (!await _reading.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return Status();
        }

        try
        {
            if (_pending is { IsCompleted: false })
            {
                Volatile.Write(ref _evidence, new(null, 0, "timeout", []));
                return Status();
            }

            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(TimeSpan.FromSeconds(2));
            // Bound even a synchronous file open or a directory that ignores cancellation. Never overlap stuck work.
            var token = budget.Token;
            _pending = Task.Run(() => CompareAsync(token), CancellationToken.None);
            try
            {
                Volatile.Write(ref _evidence, await _pending.WaitAsync(budget.Token).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Volatile.Write(ref _evidence, new(null, 0, "timeout", []));
            }
        }
        finally
        {
            _reading.Release();
        }

        return Status();
    }

    private async Task<Evidence> CompareAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset? observed = null;
        long generation = 0;
        TopologyPeer[] runtime = [];
        try
        {
            runtime = Canonical(_services.GetRequiredService<IRuntimePeerTopology>().ConfiguredPeers().Select(Identity)
                .Append(new TopologyPeer(_options.MachineId, _options.Region, _bound.Address.ToString())));
            var bytes = await ReadBoundedAsync(Path.Combine(Path.GetDirectoryName(_options.PeersFile)!, "topology.json"), cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(bytes);
            RejectDuplicateProperties(document.RootElement);
            var manifest = document.RootElement.Deserialize(TerraBaseJsonContext.Default.TopologyManifest) ?? throw new JsonException();
            var stamp = document.RootElement.GetProperty("observedAt").GetString();
            if (stamp is not { Length: >= 20 } || stamp[10] != 'T' || !(stamp.EndsWith('Z') || stamp.EndsWith("+00:00", StringComparison.Ordinal))
                || manifest.Generation <= 0 || manifest.ObservedAt.Offset != TimeSpan.Zero || manifest.Regions is not { Length: > 0 and <= MaxPeers }
                || manifest.Peers is not { Length: > 0 and <= MaxPeers }
                || manifest.Regions.Any(region => !TerraBaseOptions.IsRegion(region))
                || manifest.Regions.Distinct(StringComparer.Ordinal).Count() != manifest.Regions.Length)
            {
                throw new JsonException();
            }

            var expected = Canonical(manifest.Peers);
            if (!manifest.Regions.Order(StringComparer.Ordinal).SequenceEqual(expected.Select(peer => peer.Region).Order(StringComparer.Ordinal))
                || !expected.Any(peer => peer.Node == _options.MachineId && peer.Region == _options.Region))
            {
                throw new JsonException();
            }

            (observed, generation) = (manifest.ObservedAt, manifest.Generation);
            var lines = Encoding.UTF8.GetString(await ReadBoundedAsync(_options.PeersFile!, cancellationToken).ConfigureAwait(false))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var file = Canonical(lines.Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Select(fields => fields.Length == 3 ? new TopologyPeer(fields[0], fields[1], fields[2]) : throw new JsonException()));
            var directory = _services.GetService<IMemberDirectory>() ?? throw new JsonException();
            var listed = Canonical((await directory.ListAsync(cancellationToken).ConfigureAwait(false)).Select(Identity));
            var view = await _membership.RefreshAsync(cancellationToken).ConfigureAwait(false);
            var membership = Canonical(view.Members.Concat(view.Elsewhere.IsDefault ? [] : view.Elsewhere).Select(Identity));
            return new(observed, generation,
                expected.SequenceEqual(file) && expected.SequenceEqual(listed) && expected.SequenceEqual(membership)
                    && expected.SequenceEqual(runtime) && _membership.HoldsLease
                    && expected.Single(peer => peer.Node == _options.MachineId).Address == _bound.Address.ToString() ? "ok" : "mismatch", runtime);
        }
        catch (Exception)
        {
            // A bad provider, cancellation or malformed input must not terminate the periodic monitor or expose values.
            return new(observed, generation, "invalid", runtime);
        }
    }

    private static TopologyPeer Identity(ClusterMember member) =>
        new(member.Name, member.Region, new Uri(member.PeerAddress).Host);

    private static TopologyPeer[] Canonical(IEnumerable<TopologyPeer> peers)
    {
        var result = peers.Take(MaxPeers + 1).ToArray();
        if (result.Length is 0 or > MaxPeers || result.Any(peer => peer is null || !TerraBaseOptions.IsMachineId(peer.Node)
            || !TerraBaseOptions.IsRegion(peer.Region) || !IPAddress.TryParse(peer.Address, out var address)
            || address.AddressFamily != AddressFamily.InterNetwork || address.ToString() != peer.Address)
            || result.Select(peer => peer.Node).Distinct(StringComparer.Ordinal).Count() != result.Length
            || result.Select(peer => peer.Region).Distinct(StringComparer.Ordinal).Count() != result.Length
            || result.Select(peer => peer.Address).Distinct(StringComparer.Ordinal).Count() != result.Length)
        {
            throw new JsonException();
        }

        return [.. result.OrderBy(peer => peer.Node, StringComparer.Ordinal)];
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new JsonException();
                }

                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                RejectDuplicateProperties(item);
            }
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(string path, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
        var bytes = new byte[MaxBytes + 1];
        var count = await file.ReadAtLeastAsync(bytes, bytes.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
        return count > MaxBytes ? throw new JsonException() : bytes[..count];
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5), _time);
        do
        {
            _ = await ReadAsync(stoppingToken).ConfigureAwait(false);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    public override void Dispose()
    {
        _reading.Dispose();
        base.Dispose();
    }
}
