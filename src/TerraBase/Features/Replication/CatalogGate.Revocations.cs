using System.Text.Json;
using TerraBase.Engine;
using TerraBase.Engine.Cluster;
using TerraBase.Engine.Lattices;
using TerraBase.Features.Backup;
using ZLogger;

namespace TerraBase.Features.Replication;

internal sealed partial class CatalogGate
{
    internal const string Revocations = "catalog_revocations";

    // A revocation snapshot stages the whole copy before atomic replay. The first attempt is short; a failed one waits
    // before the next while the streams deliver the same markers. Each cut-off attempt doubles the next one's
    // budget up to the snapshot's own deadline, so a fresh catalog or one with an offline witness still completes.
    internal static readonly TimeSpan SnapshotBudget = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxSnapshotBudget = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan FirstSnapshotRetry = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MaxSnapshotRetry = TimeSpan.FromMinutes(2);
    private readonly LocalReplica _self;
    private readonly IServiceProvider _services;
    private Ulid _anchoredIncarnation;
    private RevocationCut? _revocation;
    private volatile string? _revocationStage;
    private string? _lastMissingRevocations;
    private DateTimeOffset? _lastMissingRevocationsAt;
    private long _snapshotRetryAt;
    private TimeSpan _snapshotRetry;
    private TimeSpan _snapshotBudget = SnapshotBudget;
    private readonly record struct RevocationCut(ReplicaId Replica, Hlc Stamp);

    // Written after the destructive transaction and before its external anchor/response. The stream cannot acknowledge
    // this marker without holding that earlier transaction. A state-only anti-entropy join is deliberately insufficient.
    private async Task<RevocationCut> MarkRevocationAsync(AnchorSteps steps, CancellationToken cancellationToken)
    {
        steps.Next("taking the catalog's writer");
        var store = await _engine.OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await store.BeginDurableTransactionAsync(cancellationToken).ConfigureAwait(false);
        steps.Next("writing the revocation marker");
        var body = System.Text.Encoding.UTF8.GetBytes($$"""{"replica":{{_self.Id.Value}},"marker":"{{TerraBase.Primitives.EntityIds.New(_time)}}"}""");
        var stamp = await transaction.PutAsync(Revocations, _options.MachineId, body, cancellationToken).ConfigureAwait(false);
        steps.Next("committing the revocation marker");
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
            _revocationStage = "reading catalog revocation membership";
            HashSet<string> names = [.. listed.Select(peer => peer.Name), _options.MachineId];
            if (_engine.SystemDatabaseExists)
            {
                names.UnionWith((await RecordedAsync(_engine, cancellationToken).ConfigureAwait(false)).Keys);
            }
            // The volume's old membership can omit a machine added after the snapshot. Include the external anchors too.
            _revocationStage = "listing external catalog revocation anchors";
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
            // The machine whose anchor names each cut: it holds its own cut first-hand (its snapshot exports its local cut).
            var writers = new Dictionary<ReplicaId, string>();
            var legacy = false;
            foreach (var name in names.Order(StringComparer.Ordinal))
            {
                // This node's own start head/identity was already checked against this anchor. Its exact old-epoch
                // cut was persisted under that same file's writer, without inferring coverage from a newer replica id.
                if (name == _options.MachineId && _anchorHeld && _restored == false)
                {
                    continue;
                }

                var key = "catalog-anchor/" + name;
                _revocationStage = $"reading external catalog revocation anchor for {name}";
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

                    var cut = ReadRevocation(root);
                    cuts.Add(cut);
                    writers[cut.Replica] = name;
                }, cancellationToken).ConfigureAwait(false);
                // A seeded machine has no own anchor yet and still needs the existing all-holder admission below.
                // Every other known machine's external witness must be readable; no missing witness grants coverage.
                if (!found && !_fresh && name != _options.MachineId)
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

            _revocationStage = "checking external catalog revocation coverage";
            var missing = await MissingRevocationsAsync(cuts, cancellationToken).ConfigureAwait(false);
            if (missing.Count == 0)
            {
                (_snapshotRetry, _snapshotBudget) = (TimeSpan.Zero, SnapshotBudget);
                return true;
            }

            if (_snapshotRetry > TimeSpan.Zero && _time.GetTimestamp() < _snapshotRetryAt)
            {
                Pending = [.. Pending, "external revocations not replicated yet"];
                return false;
            }

            // Copy every state and its vector atomically from a trusted witness; independently compared roots or a marker
            // joined on its own cannot prove the earlier deletion. No witness leaves the catalog closed.
            var origins = cuts.Select(cut => cut.Replica.Value).ToHashSet();
            var attempted = false;
            using (var deadline = new CancellationTokenSource(_snapshotBudget, _time))
            using (var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token))
            {
                try
                {
                    // The writers of the missing cuts first: another machine holds a cut only as far as it received it (fra,
                    // 2026-10-25: gru held sjc's changes to 17:29 while sjc's anchor named 18:58, and every pass tried gru only).
                    var first = missing.Select(cut => writers.GetValueOrDefault(cut.Replica)).OfType<string>().ToHashSet(StringComparer.Ordinal);
                    foreach (var peer in listed.Where(peer => _verified.Contains(peer.Name)).OrderBy(peer => first.Contains(peer.Name) ? 0 : 1))
                    {
                        attempted = true;
                        _revocationStage = $"importing catalog revocation snapshot from {peer.Name}";
                        _logger.ZLogInformation($"Importing a catalog revocation snapshot from {peer.Name:@Peer} for {origins.Count} origins");
                        bool imported;
                        try
                        {
                            imported = await _services.GetRequiredService<ReplicaSnapshots>().PullAsync(ChangeGroups.System, peer, origins, budget.Token).ConfigureAwait(false);
                        }
                        catch (Exception error) when (!budget.IsCancellationRequested)
                        {
                            // One witness failing must not keep the others from being tried. The type only: provider errors may carry credentials.
                            _logger.ZLogWarning($"A catalog revocation snapshot from {peer.Name:@Peer} failed ({error.GetType().Name:@Error}); the gate tries the next verified machine");
                            continue;
                        }

                        _revocationStage = $"checking catalog revocation coverage after snapshot from {peer.Name}";
                        _logger.ZLogInformation($"Catalog revocation snapshot from {peer.Name:@Peer} finished with imported={imported}");
                        if ((await MissingRevocationsAsync(cuts, cancellationToken).ConfigureAwait(false)).Count == 0)
                        {
                            (_snapshotRetry, _snapshotBudget) = (TimeSpan.Zero, SnapshotBudget);
                            return true;
                        }
                    }
                }
                catch (Exception error) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    // An import cancelled mid-copy rolls back and releases the writer; the streams keep delivering meanwhile.
                    _logger.ZLogWarning(error, $"A catalog revocation snapshot did not finish within {_snapshotBudget.TotalSeconds}s; the gate waits for replication and tries again with longer");
                    _snapshotBudget = TimeSpan.FromTicks(Math.Min(_snapshotBudget.Ticks * 2, MaxSnapshotBudget.Ticks));
                }
            }

            // No verified machine yet: nothing was copied, so the next pass may try at once.
            if (attempted)
            {
                _snapshotRetry = _snapshotRetry == TimeSpan.Zero ? FirstSnapshotRetry : TimeSpan.FromTicks(Math.Min(_snapshotRetry.Ticks * 2, MaxSnapshotRetry.Ticks));
                _snapshotRetryAt = _time.GetTimestamp() + (long)(_snapshotRetry.TotalSeconds * _time.TimestampFrequency);
            }

            Pending = [.. Pending, "external revocations not covered"];
            return false;
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            // Provider/key failures may contain credentials. Classify without logging their text.
            Pending = [.. Pending, $"external revocations unreadable ({error.GetType().Name})"];
            return false;
        }
        finally
        {
            _revocationStage = null;
        }
    }

    // The cuts this catalog does not cover yet; empty when it covers them all.
    private async Task<IReadOnlyList<RevocationCut>> MissingRevocationsAsync(IReadOnlyList<RevocationCut> cuts, CancellationToken cancellationToken)
    {
        if (cuts.Count == 0)
        {
            return [];
        }

        var store = await _engine.OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await store.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false);
        var applied = await transaction.GetAppliedAsync(cancellationToken).ConfigureAwait(false);
        var missing = cuts.Where(cut => !applied.TryGetValue(cut.Replica, out var stamp) || stamp.Value < cut.Stamp.Value).ToArray();
        if (missing.Length > 0)
        {
            // Only replica identities and causal stamps: no catalog bodies, object-store errors or credentials.
            var evidence = string.Join(",", missing.Select(cut => $"{cut.Replica.Value:x}:{applied.GetValueOrDefault(cut.Replica).Value}/{cut.Stamp.Value}"));
            var now = _time.GetUtcNow();
            if (evidence != _lastMissingRevocations
                && (_lastMissingRevocationsAt is not { } last || now - last >= TimeSpan.FromSeconds(30)))
            {
                _lastMissingRevocations = evidence;
                _lastMissingRevocationsAt = now;
                _logger.ZLogInformation($"Catalog revocation coverage is missing {missing.Length} cuts (replica:held/required): {evidence:@Cuts}");
            }
        }
        return missing;
    }
}
