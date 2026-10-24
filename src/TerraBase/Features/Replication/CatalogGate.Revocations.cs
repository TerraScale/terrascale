using System.Text.Json;
using TerraBase.Engine;
using TerraBase.Engine.Cluster;
using TerraBase.Engine.Lattices;
using TerraBase.Features.Backup;

namespace TerraBase.Features.Replication;

internal sealed partial class CatalogGate
{
    internal const string Revocations = "catalog_revocations";
    private readonly LocalReplica _self;
    private readonly IServiceProvider _services;
    private Ulid _anchoredIncarnation;
    private RevocationCut? _revocation;
    private readonly record struct RevocationCut(ReplicaId Replica, Hlc Stamp);

    // Written after the destructive transaction and before its external anchor/response. The stream cannot acknowledge
    // this marker without holding that earlier transaction. A state-only anti-entropy join is deliberately insufficient.
    private async Task<RevocationCut> MarkRevocationAsync(CancellationToken cancellationToken)
    {
        var store = await _engine.OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await store.BeginDurableTransactionAsync(cancellationToken).ConfigureAwait(false);
        var body = System.Text.Encoding.UTF8.GetBytes($$"""{"replica":{{_self.Id.Value}},"marker":"{{TerraBase.Primitives.EntityIds.New(_time)}}"}""");
        var stamp = await transaction.PutAsync(Revocations, _options.MachineId, body, cancellationToken).ConfigureAwait(false);
        _ = await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        // Capture this exact commit, rather than rereading a row a delayed replica join might subsequently replace.
        return new RevocationCut(_self.Id, new Hlc(unchecked((ulong)stamp)));
    }

    private static RevocationCut ReadRevocation(JsonElement root)
    {
        if (root.GetProperty("revocations").GetInt32() != 1)
        {
            throw new InvalidDataException("unsupported catalog revocation evidence");
        }

        var cut = new RevocationCut(new ReplicaId(root.GetProperty("replica").GetUInt64()), new Hlc(root.GetProperty("stamp").GetUInt64()));
        if (cut.Replica.Value == 0 || cut.Stamp.Value == 0 || cut.Stamp.Value > long.MaxValue)
        {
            throw new InvalidDataException("invalid catalog revocation cut");
        }

        return cut;
    }

    private async Task<bool> RevocationsHeldAsync(IReadOnlyCollection<ClusterMember> listed, CancellationToken cancellationToken)
    {
        if (_anchors is null)
        {
            return true;
        }

        try
        {
            HashSet<string> names = [.. listed.Select(peer => peer.Name), _options.MachineId];
            if (_engine.SystemDatabaseExists)
            {
                names.UnionWith((await RecordedAsync(_engine, cancellationToken).ConfigureAwait(false)).Keys);
            }
            // The volume's old membership can omit a machine added after the snapshot. Include the external anchors too.
            await foreach (var item in _anchors.ListAsync("catalog-anchor/", null, cancellationToken).ConfigureAwait(false))
            {
                if (!item.Key.StartsWith("catalog-anchor/", StringComparison.Ordinal) || !names.Add(item.Key["catalog-anchor/".Length..]))
                {
                    continue;
                }

                if (names.Count > MaxMembers)
                {
                    throw new InvalidDataException("too many catalog anchors");
                }
            }

            if (names.Count > MaxMembers)
            {
                throw new InvalidDataException("too many catalog anchors");
            }

            List<RevocationCut> cuts = [];
            var legacy = false;
            foreach (var name in names.Order(StringComparer.Ordinal))
            {
                // This node's own start head/identity was already checked against this anchor. Its own old epoch need not
                // be inferred from a newer replica id, nor inserted into received coverage.
                if (name == _options.MachineId && _anchorHeld && _restored == false)
                {
                    continue;
                }

                var key = "catalog-anchor/" + name;
                var found = await _anchors.ReadAsync(key, async (stream, token) =>
                {
                    using var plain = new AnchorBody();
                    await BackupObject.ReadAsync(stream, plain, key, id => _keys!.GetAsync(KeyScope, id, token), token).ConfigureAwait(false);
                    using var json = JsonDocument.Parse(plain.ToArray());
                    var root = json.RootElement;
                    if (root.GetProperty("version").GetInt32() != 1)
                    {
                        throw new InvalidDataException("unsupported catalog anchor");
                    }

                    if (!root.TryGetProperty("revocations", out _))
                    {
                        legacy = true;
                        return;
                    }

                    cuts.Add(ReadRevocation(root));
                }, cancellationToken).ConfigureAwait(false);
                if (!found && !_fresh)
                {
                    throw new InvalidDataException("a catalog anchor is missing");
                }
            }

            // During a rolling upgrade an old anchor has no causal frontier. All recorded holders must still vouch;
            // a majority of old catalogs cannot make that absence into proof. The first upgraded anchor adds a witness.
            if (legacy && names.Any(name => name != _options.MachineId && !_verified.Contains(name)))
            {
                Pending = [.. Pending, "legacy revocation anchors require all holders"];
                return false;
            }

            if (await CoversRevocationsAsync(cuts, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            // Copy every state and its vector atomically from a trusted witness; independently compared roots or a marker
            // joined on its own cannot prove the earlier deletion. No witness leaves the catalog closed.
            var origins = cuts.Select(cut => cut.Replica.Value).ToHashSet();
            foreach (var peer in listed.Where(peer => _verified.Contains(peer.Name)))
            {
                _ = await _services.GetRequiredService<ReplicaSnapshots>().PullAsync(ChangeGroups.System, peer, origins, cancellationToken).ConfigureAwait(false);
                if (await CoversRevocationsAsync(cuts, cancellationToken).ConfigureAwait(false))
                {
                    return true;
                }
            }

            Pending = [.. Pending, "external revocations not covered"];
            return false;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Provider/key failures may contain credentials. Classify without logging their text.
            Pending = [.. Pending, "external revocations unreadable"];
            return false;
        }
    }

    private async Task<bool> CoversRevocationsAsync(IReadOnlyList<RevocationCut> cuts, CancellationToken cancellationToken)
    {
        if (cuts.Count == 0)
        {
            return true;
        }

        var store = await _engine.OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await store.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false);
        var applied = await transaction.GetAppliedAsync(cancellationToken).ConfigureAwait(false);
        return cuts.All(cut => applied.TryGetValue(cut.Replica, out var stamp) && stamp.Value >= cut.Stamp.Value);
    }
}
