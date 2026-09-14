using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;

namespace TerraBase.Replication;

/// <summary>A member eligible for deterministic gossip fan-out.</summary>
public sealed record GossipPeer
{
    public GossipPeer(string id, ulong epoch, bool healthy = true)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A gossip peer requires an id.", nameof(id));
        Id = id;
        Epoch = epoch;
        Healthy = healthy;
    }

    public string Id { get; }
    public ulong Epoch { get; init; }
    public bool Healthy { get; init; }
}

/// <summary>A version-vector digest exchanged by a gossip round.</summary>
public sealed record GossipDigest
{
    public GossipDigest(string sender, ulong membershipEpoch, IReadOnlyDictionary<string, ulong> frontier, string digest)
    {
        if (string.IsNullOrWhiteSpace(sender)) throw new ArgumentException("A sender is required.", nameof(sender));
        Sender = sender;
        MembershipEpoch = membershipEpoch;
        Frontier = new ReadOnlyDictionary<string, ulong>(CausalVersion.CopyContext(frontier));
        Digest = digest ?? throw new ArgumentNullException(nameof(digest));
    }

    public string Sender { get; }
    public ulong MembershipEpoch { get; }
    public IReadOnlyDictionary<string, ulong> Frontier { get; }
    public string Digest { get; }
}

/// <summary>An idempotent gossip envelope identified independently of payload order.</summary>
public sealed record GossipEnvelope
{
    public GossipEnvelope(string messageId, string sender, ulong membershipEpoch, IReadOnlyList<ReplicationRecord> records)
    {
        if (string.IsNullOrWhiteSpace(messageId)) throw new ArgumentException("A message id is required.", nameof(messageId));
        if (string.IsNullOrWhiteSpace(sender)) throw new ArgumentException("A sender is required.", nameof(sender));
        MessageId = messageId;
        Sender = sender;
        MembershipEpoch = membershipEpoch;
        Records = records?.OrderBy(record => record.Dot.Origin, StringComparer.Ordinal).ThenBy(record => record.Dot.Sequence).ToArray()
            ?? throw new ArgumentNullException(nameof(records));
    }

    public string MessageId { get; }
    public string Sender { get; }
    public ulong MembershipEpoch { get; }
    public IReadOnlyList<ReplicationRecord> Records { get; }
}

/// <summary>
/// Deterministic gossip membership and duplicate suppression. Membership changes
/// are epoch fenced; a peer cannot inject frames from an earlier view.
/// </summary>
public sealed class GossipEngine
{
    private readonly Dictionary<string, GossipPeer> peers = new(StringComparer.Ordinal);
    private readonly HashSet<string> seenMessages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ulong> frontier = new(StringComparer.Ordinal);
    private readonly object gate = new();
    private ulong epoch;

    public GossipEngine(string localPeer, ulong membershipEpoch = 1)
    {
        if (string.IsNullOrWhiteSpace(localPeer)) throw new ArgumentException("A local peer is required.", nameof(localPeer));
        LocalPeer = localPeer;
        epoch = membershipEpoch == 0 ? throw new ArgumentOutOfRangeException(nameof(membershipEpoch)) : membershipEpoch;
        peers[localPeer] = new GossipPeer(localPeer, epoch);
    }

    public string LocalPeer { get; }
    public ulong MembershipEpoch { get { lock (gate) return epoch; } }
    public IReadOnlyList<GossipPeer> Peers
    {
        get { lock (gate) return peers.Values.OrderBy(peer => peer.Id, StringComparer.Ordinal).ToArray(); }
    }

    public void SetMembership(ulong membershipEpoch, IEnumerable<GossipPeer> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        if (membershipEpoch == 0) throw new ArgumentOutOfRangeException(nameof(membershipEpoch));
        var incoming = members.ToDictionary(member => member.Id, StringComparer.Ordinal);
        if (!incoming.ContainsKey(LocalPeer)) throw new InvalidOperationException("Membership must contain the local peer.");
        lock (gate)
        {
            if (membershipEpoch < epoch) throw new InvalidOperationException("A stale membership epoch is fenced.");
            epoch = membershipEpoch;
            peers.Clear();
            foreach (var member in incoming) peers.Add(member.Key, member.Value with { Epoch = membershipEpoch });
        }
    }

    public void Observe(ReplicationDot dot)
    {
        if (dot.Sequence == 0 || string.IsNullOrWhiteSpace(dot.Origin)) throw new ArgumentException("A valid dot is required.", nameof(dot));
        lock (gate) CausalVersion.Advance(frontier, dot);
    }

    public GossipDigest CreateDigest()
    {
        lock (gate)
        {
            var values = new ReadOnlyDictionary<string, ulong>(new Dictionary<string, ulong>(frontier, StringComparer.Ordinal));
            return new(LocalPeer, epoch, values, DigestOf(values));
        }
    }

    /// <summary>Returns peers in a stable rendezvous order for a gossip round.</summary>
    public IReadOnlyList<GossipPeer> SelectTargets(string roundId, int fanout)
    {
        if (string.IsNullOrWhiteSpace(roundId)) throw new ArgumentException("A round id is required.", nameof(roundId));
        if (fanout < 0) throw new ArgumentOutOfRangeException(nameof(fanout));
        lock (gate)
        {
            return peers.Values.Where(peer => peer.Id != LocalPeer && peer.Healthy && peer.Epoch == epoch)
                .OrderBy(peer => Score(roundId, peer.Id), StringComparer.Ordinal)
                .Take(fanout)
                .ToArray();
        }
    }

    /// <summary>Accepts a message once and rejects a stale membership epoch.</summary>
    public bool Accept(GossipEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        lock (gate)
        {
            if (envelope.MembershipEpoch != epoch || !peers.ContainsKey(envelope.Sender)) return false;
            return seenMessages.Add(envelope.MessageId);
        }
    }

    public IReadOnlyDictionary<string, ulong> MissingFrom(GossipDigest peer)
    {
        ArgumentNullException.ThrowIfNull(peer);
        lock (gate)
        {
            var missing = new Dictionary<string, ulong>(StringComparer.Ordinal);
            foreach (var pair in peer.Frontier)
            {
                var local = frontier.GetValueOrDefault(pair.Key);
                if (pair.Value > local) missing[pair.Key] = pair.Value;
            }
            return new ReadOnlyDictionary<string, ulong>(missing);
        }
    }

    private static string Score(string round, string peer) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(round + "\0" + peer)));

    private static string DigestOf(IReadOnlyDictionary<string, ulong> values)
    {
        var bytes = Encoding.UTF8.GetBytes(string.Join('|', values.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + ":" + pair.Value)));
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}

/// <summary>Thresholds for adaptive hot-key replication.</summary>
public sealed record HotKeyReplicationPolicy
{
    public HotKeyReplicationPolicy(int heatThreshold, int maxReplicas, int coolingRounds = 3)
    {
        if (heatThreshold <= 0) throw new ArgumentOutOfRangeException(nameof(heatThreshold));
        if (maxReplicas <= 0) throw new ArgumentOutOfRangeException(nameof(maxReplicas));
        if (coolingRounds <= 0) throw new ArgumentOutOfRangeException(nameof(coolingRounds));
        HeatThreshold = heatThreshold;
        MaxReplicas = maxReplicas;
        CoolingRounds = coolingRounds;
    }

    public int HeatThreshold { get; }
    public int MaxReplicas { get; }
    public int CoolingRounds { get; }
}

/// <summary>The deterministic placement decision for one key.</summary>
public sealed record HotKeyReplicationPlan(
    string Key,
    string Primary,
    IReadOnlyList<string> Replicas,
    int Heat,
    bool IsHot,
    int WriteQuorum);

/// <summary>Tracks bounded per-round key heat and expands hot keys onto stable replicas.</summary>
public sealed class HotKeyReplicator
{
    private readonly HotKeyReplicationPolicy policy;
    private readonly Dictionary<string, int> heat = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> quietRounds = new(StringComparer.Ordinal);
    private readonly object gate = new();

    public HotKeyReplicator(HotKeyReplicationPolicy policy) => this.policy = policy ?? throw new ArgumentNullException(nameof(policy));

    public void Observe(string key, int operations = 1)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("A key is required.", nameof(key));
        if (operations <= 0) throw new ArgumentOutOfRangeException(nameof(operations));
        lock (gate)
        {
            heat[key] = checked(heat.GetValueOrDefault(key) + operations);
            quietRounds[key] = 0;
        }
    }

    public void AdvanceRound()
    {
        lock (gate)
        {
            foreach (var key in heat.Keys.ToArray())
            {
                if (heat[key] == 0) quietRounds[key] = quietRounds.GetValueOrDefault(key) + 1;
                heat[key] = 0;
            }
        }
    }

    public HotKeyReplicationPlan Plan(string key, string primary, IEnumerable<string> members)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("A key is required.", nameof(key));
        if (string.IsNullOrWhiteSpace(primary)) throw new ArgumentException("A primary is required.", nameof(primary));
        ArgumentNullException.ThrowIfNull(members);
        var nodes = members.Append(primary).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        lock (gate)
        {
            var currentHeat = heat.GetValueOrDefault(key);
            var hot = currentHeat >= policy.HeatThreshold;
            var replicas = hot
                ? nodes.Where(node => node != primary).OrderBy(node => Score(key, node), StringComparer.Ordinal).Take(policy.MaxReplicas).ToArray()
                : [];
            return new(key, primary, replicas, currentHeat, hot, 1 + replicas.Length / 2);
        }
    }

    private static string Score(string key, string node) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key + "\0" + node)));
}
