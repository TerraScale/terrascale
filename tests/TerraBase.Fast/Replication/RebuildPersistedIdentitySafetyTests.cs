// Draft destination: tests/TerraBase.Fast/Replication/RebuildPersistedIdentitySafetyTests.cs
// Requires A4 transactional pins + the PR2 ledger + this draft's classified incarnation getter/barrier patch.
using System.Globalization;
using System.Net;
using Bogus;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Engine.Turso;
using TerraBase.Features.Replication;
using TerraBase.Features.Replication.V1;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

public sealed class RebuildPersistedIdentitySafetyTests
{
    public enum InvalidIdentity { Missing, Malformed, Empty, Overflow, Noncanonical }
    public enum IdentityOperation { Repair, RefillPage, Refill, Completion, CompletionPublication, CertifiedRetry, Finalization }

    [Test]
    [Arguments(IdentityOperation.Repair, InvalidIdentity.Missing)]
    [Arguments(IdentityOperation.Repair, InvalidIdentity.Malformed)]
    [Arguments(IdentityOperation.RefillPage, InvalidIdentity.Missing)]
    [Arguments(IdentityOperation.RefillPage, InvalidIdentity.Malformed)]
    [Arguments(IdentityOperation.Refill, InvalidIdentity.Missing)]
    [Arguments(IdentityOperation.Refill, InvalidIdentity.Malformed)]
    [Arguments(IdentityOperation.Completion, InvalidIdentity.Missing)]
    [Arguments(IdentityOperation.Completion, InvalidIdentity.Malformed)]
    [Arguments(IdentityOperation.CompletionPublication, InvalidIdentity.Missing)]
    [Arguments(IdentityOperation.CompletionPublication, InvalidIdentity.Malformed)]
    [Arguments(IdentityOperation.CertifiedRetry, InvalidIdentity.Missing)]
    [Arguments(IdentityOperation.CertifiedRetry, InvalidIdentity.Malformed)]
    [Arguments(IdentityOperation.CertifiedRetry, InvalidIdentity.Empty)]
    [Arguments(IdentityOperation.CertifiedRetry, InvalidIdentity.Overflow)]
    [Arguments(IdentityOperation.CertifiedRetry, InvalidIdentity.Noncanonical)]
    [Arguments(IdentityOperation.Finalization, InvalidIdentity.Missing)]
    [Arguments(IdentityOperation.Finalization, InvalidIdentity.Malformed)]
    public async Task Invalid_persisted_receiver_identity_faults_the_exact_copy_and_keeps_every_retained_byte(
        IdentityOperation operation, InvalidIdentity identity)
    {
        var completing = operation is not (IdentityOperation.Repair or IdentityOperation.RefillPage or IdentityOperation.Refill);
        await using var fixture = await Fixture.StartAsync(salvaged: completing);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var certified = operation is IdentityOperation.CertifiedRetry or IdentityOperation.Finalization;
        if (certified)
        {
            await Assert.That(await fixture.Repair.CompleteRebuildAsync(fixture.Database, fixture.Original, fixture.Rebuilt, deadline.Token)).IsTrue();
            await using var write = await fixture.Copy.BeginDurableTransactionAsync(deadline.Token);
            _ = await write.PutAsync("items", "before-detection", "{\"n\":2}"u8.ToArray(), deadline.Token);
            _ = await write.CommitAsync(deadline.Token);
        }
        byte[] keptBefore;
        await using (var read = await fixture.Copy.BeginTransactionAsync(write: false, deadline.Token))
        {
            keptBefore = (await read.GetAsync("items", "kept", deadline.Token))!.Json.ToArray();
        }
        var states = await RepairStatesAsync(fixture, deadline.Token);
        var damaged = 0;
        if (operation == IdentityOperation.CompletionPublication)
        {
            // The FULL transaction has disposed; the publication read must detect the new invalid identity.
            fixture.Checkpoint = (step, database) =>
            {
                if (step == RebuildLedger.BarrierDisposed && database == fixture.Database)
                {
                    DamageIdentityAsync(fixture.Copy, identity, deadline.Token).GetAwaiter().GetResult();
                    damaged++;
                }
            };
        }
        else
        {
            await DamageIdentityAsync(fixture.Copy, identity, deadline.Token);
            damaged++;
        }
        try
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            {
                switch (operation)
                {
                    case IdentityOperation.Repair:
                        _ = await fixture.Repair.RepairRebuildAsync(fixture.Database, fixture.Rebuilt,
                            [states[0] with { Id = "must-not-land" }], deadline.Token);
                        break;
                    case IdentityOperation.RefillPage:
                        _ = await fixture.Repair.RefillRebuildAsync(fixture.Database, fixture.Rebuilt,
                            [DocumentStateEncoding.Bucket(states[0].Collection, states[0].Id)], null, [states[0]], default, deadline.Token);
                        break;
                    case IdentityOperation.Refill:
                        _ = await fixture.Repair.RecordRefillAsync(fixture.Database, fixture.Rebuilt, "identity-source", fixture.Source, 1, default, deadline.Token);
                        break;
                    case IdentityOperation.Finalization:
                        _ = await fixture.Storage.FinalizeAsync(fixture.Database, fixture.Rebuilt, fixture.Side, deadline.Token);
                        break;
                    default:
                        _ = await fixture.Repair.CompleteRebuildAsync(fixture.Database, fixture.Original, fixture.Rebuilt, deadline.Token);
                        break;
                }
            });
            await Assert.That(StoredDataCorruption.Is(error!)).IsTrue();
        }
        finally
        {
            fixture.Checkpoint = null;
        }
        await Assert.That(damaged).IsEqualTo(1);
        await Assert.That(fixture.Copy.Incarnation).IsEqualTo(fixture.Rebuilt).Because("fault recording must retain the cached identity of the exact copy that read invalid storage");
        await Assert.That(fixture.Copy.CopyFault).IsNotNull();
        var pending = await fixture.Ledger.GetAsync(fixture.Database, deadline.Token);
        await Assert.That(pending?.Phase).IsEqualTo(RebuildPhase.Pending);
        await Assert.That(pending?.OldIncarnation).IsEqualTo(certified ? fixture.Rebuilt : fixture.Original);
        await Assert.That(pending?.AdditionalIncarnation).IsEqualTo(certified ? fixture.Original : fixture.Rebuilt);
        await Assert.That(pending?.NewIncarnation).IsNull();
        await AssertDurablePendingWithoutRefillAsync(fixture, deadline.Token);
        await Assert.That(fixture.Copy.RebuildRow?.Refill).IsNull();
        await AssertOrdinaryWriteRefusedAsync(fixture.Copy, deadline.Token);
        await Assert.That(await fixture.Repair.RepairRebuildAsync(fixture.Database, fixture.Rebuilt, states, deadline.Token)).IsNull();
        await Assert.That(await fixture.Repair.RecordRefillAsync(fixture.Database, fixture.Rebuilt, "identity-source", fixture.Source, 1, default, deadline.Token)).IsFalse();
        await Assert.That(await fixture.Repair.CompleteRebuildAsync(fixture.Database, fixture.Original, fixture.Rebuilt, deadline.Token)).IsFalse();
        await Assert.That(await fixture.Storage.FinalizeAsync(fixture.Database, fixture.Rebuilt, fixture.Side, deadline.Token)).IsFalse();
        await using (var read = await fixture.Copy.BeginTransactionAsync(write: false, deadline.Token))
        {
            await Assert.That(await read.GetAsync("items", "must-not-land", deadline.Token)).IsNull();
            await Assert.That((await read.GetAsync("items", "kept", deadline.Token))!.Json.ToArray().SequenceEqual(keptBefore)).IsTrue();
        }
        await AssertSideKeptAsync(fixture, deadline.Token);
    }

    [Test]
    public async Task A_different_valid_persisted_identity_is_a_conditional_mismatch_without_a_copy_fault()
    {
        await using var fixture = await Fixture.StartAsync(salvaged: true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await Assert.That(await fixture.Repair.CompleteRebuildAsync(fixture.Database, fixture.Original, fixture.Rebuilt, deadline.Token)).IsTrue();
        var certified = await fixture.Ledger.GetAsync(fixture.Database, deadline.Token);
        await ChangeIdentityAsync(fixture.Copy, Ulid.NewUlid().ToString(), deadline.Token);
        await Assert.That(await fixture.Repair.CompleteRebuildAsync(fixture.Database, fixture.Original, fixture.Rebuilt, deadline.Token)).IsFalse();
        await Assert.That(await fixture.Storage.FinalizeAsync(fixture.Database, fixture.Rebuilt, fixture.Side, deadline.Token)).IsFalse();
        await Assert.That(fixture.Copy.CopyFault).IsNull();
        await Assert.That(await fixture.Ledger.GetAsync(fixture.Database, deadline.Token)).IsEqualTo(certified);
        await AssertSideKeptAsync(fixture, deadline.Token);
    }

    [Test]
    [Arguments(InvalidIdentity.Missing)]
    [Arguments(InvalidIdentity.Malformed)]
    public async Task A_previously_writable_source_detects_invalid_identity_before_serving_a_page_and_durably_refuses_ACKs(InvalidIdentity identity)
    {
        await using var fixture = await Fixture.StartAsync(salvaged: false);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var copy = await fixture.CreateHealthyCopyAsync(deadline.Token);
        var database = copy.Tenant!;
        await SeedAsync(copy, deadline.Token);
        var source = fixture.Service<IChangeGroupSource>();
        var peer = ReplicaId.Of(99, 1, 0);
        var digest = await fixture.Service<IReplicaStates>().DigestAsync(database, copy.Incarnation, deadline.Token);
        await Assert.That(digest).IsNotNull();
        await Assert.That(await source.AckAsync(database, copy.Incarnation, peer, digest!.Head, default, deadline.Token)).IsTrue();
        using var channel = PeerChannel.Create(fixture.PeerAddress, PeerTls.Credential);
        var client = new Features.Replication.V1.Replication.ReplicationClient(channel);
        var request = new BucketRequest { Database = ChangeGroups.Wire(database, copy.Incarnation) };
        request.Buckets.AddRange(Enumerable.Range(0, TursoStorageTransaction.Buckets).Select(bucket => (uint)bucket));
        var before = 0;
        using (var call = client.ReadBucket(request, cancellationToken: deadline.Token))
        {
            while (await call.ResponseStream.MoveNext(deadline.Token))
            {
                before++;
            }
        }
        await Assert.That(before).IsGreaterThan(0);
        await DamageIdentityAsync(copy, identity, deadline.Token);
        var pages = 0;
        using (var call = client.ReadBucket(request, cancellationToken: deadline.Token))
        {
            _ = await Assert.ThrowsAsync<RpcException>(async () =>
            {
                while (await call.ResponseStream.MoveNext(deadline.Token))
                {
                    pages++;
                }
            });
        }
        await Assert.That(pages).IsEqualTo(0);
        await Assert.That(copy.CopyFault).IsNotNull();
        var pending = await fixture.Ledger.GetAsync(database, deadline.Token);
        await Assert.That(pending?.Phase).IsEqualTo(RebuildPhase.Pending);
        await Assert.That(pending?.OldIncarnation).IsEqualTo(copy.Incarnation);
        await Assert.That(pending?.NewIncarnation).IsNull();
        await Assert.That(await source.AckAsync(database, copy.Incarnation, peer, digest.Head, default, deadline.Token)).IsFalse();
        await Assert.That(await source.ReadAsync(database, copy.Incarnation, 0, ChangeGroups.MaxGroups, ChangeGroups.MaxBatchBytes, deadline.Token)).IsNull();
        await Assert.That(await fixture.Service<IReplicaStates>().DigestAsync(database, null, deadline.Token)).IsNull();
        await AssertOrdinaryWriteRefusedAsync(copy, deadline.Token);
        await using (var read = await copy.BeginTransactionAsync(write: false, deadline.Token))
        {
            await Assert.That((await read.GetAsync("items", "source-kept", deadline.Token))!.Json.ToArray().SequenceEqual("{\"n\":3}"u8.ToArray())).IsTrue();
        }
        await Assert.That(fixture.Engine.Ledger.FailedClosed).IsNull().Because("a recorded tenant fault must not fail unrelated tenants");
        await AssertSideKeptAsync(fixture, deadline.Token);
    }

    [Test]
    [Arguments(InvalidIdentity.Missing)]
    [Arguments(InvalidIdentity.Malformed)]
    public async Task Invalid_SYSTEM_identity_fails_the_node_closed_and_refuses_healthy_tenant_writes_and_source_ACKs(InvalidIdentity identity)
    {
        await using var fixture = await Fixture.StartAsync(salvaged: false);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var healthy = await fixture.CreateHealthyCopyAsync(deadline.Token);
        await SeedAsync(healthy, deadline.Token);
        var system = fixture.Engine.SystemStore ?? throw new InvalidOperationException("SYSTEM is missing");
        await DamageIdentityAsync(system, identity, deadline.Token);
        var error = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            _ = await fixture.Service<IReplicaStates>().DigestAsync(ChangeGroups.System, system.Incarnation, deadline.Token));
        await Assert.That(StoredDataCorruption.Is(error!)).IsTrue();
        await Assert.That(system.CopyFault).IsNotNull();
        await Assert.That(fixture.Engine.Ledger.FailedClosed).IsNotNull();
        await AssertOrdinaryWriteRefusedAsync(healthy, deadline.Token);
        _ = await Assert.ThrowsAsync<ReplicaCopyFaultedException>(async () =>
            _ = await fixture.Service<IChangeGroupSource>().AckAsync(healthy.Tenant!, healthy.Incarnation, ReplicaId.Of(99, 1, 0), 0, default, deadline.Token));
        using var response = await fixture.Client.GetAsync("/readyz", deadline.Token);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await AssertSideKeptAsync(fixture, deadline.Token);
    }

    private static async Task SeedAsync(TursoDatabaseStore copy, CancellationToken cancellationToken)
    {
        await using var write = await copy.BeginDurableTransactionAsync(cancellationToken);
        _ = await write.CreateCollectionAsync(new CollectionInfo("items", 1), cancellationToken);
        _ = await write.PutAsync("items", "source-kept", "{\"n\":3}"u8.ToArray(), cancellationToken);
        _ = await write.CommitAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<RepairState>> RepairStatesAsync(Fixture fixture, CancellationToken cancellationToken)
    {
        var working = await fixture.Storage.CopyForSalvageAsync(fixture.Side, cancellationToken);
        try
        {
            var rows = await fixture.Storage.MeasureSideAsync(working, string.Empty, string.Empty, 1024, 2 * 1024 * 1024, cancellationToken);
            return await fixture.Storage.ReadSideAsync(working, rows, cancellationToken);
        }
        finally
        {
            await fixture.Storage.DiscardWorkingCopyAsync(working, cancellationToken);
        }
    }

    private static Task DamageIdentityAsync(TursoDatabaseStore copy, InvalidIdentity identity, CancellationToken cancellationToken) =>
        ChangeIdentityAsync(copy, identity switch
        {
            InvalidIdentity.Missing => null,
            InvalidIdentity.Malformed => "not-a-ulid",
            InvalidIdentity.Empty => Ulid.Empty.ToString(),
            InvalidIdentity.Overflow => "80000000000000000000000000",
            InvalidIdentity.Noncanonical => "01arz3ndektsv4rrffq69g5fav",
            _ => throw new ArgumentOutOfRangeException(nameof(identity)),
        }, cancellationToken);

    private static async Task ChangeIdentityAsync(TursoDatabaseStore copy, string? identity, CancellationToken cancellationToken)
    {
        // Native fixture damage under the real writer, with the store's cached incarnation deliberately unchanged.
        _ = await copy.WithWriterAsync(session =>
        {
            using var command = session.Connection.CreateCommand();
            command.CommandText = identity is null ? "DELETE FROM commit_offset WHERE id = 1;"
                : "UPDATE commit_offset SET incarnation = $incarnation WHERE id = 1;";
            if (identity is not null)
            {
                command.Parameters.AddWithValue("$incarnation", identity);
            }
            return command.ExecuteNonQuery();
        }, "a test damaging persisted database identity", cancellationToken);
    }

    private static async Task AssertOrdinaryWriteRefusedAsync(TursoDatabaseStore copy, CancellationToken cancellationToken)
    {
        _ = await Assert.ThrowsAsync<ReplicaCopyFaultedException>(async () =>
        {
            await using var write = await copy.BeginDurableTransactionAsync(cancellationToken);
        });
    }

    private static async Task AssertDurablePendingWithoutRefillAsync(Fixture fixture, CancellationToken cancellationToken)
    {
        var system = fixture.Engine.SystemStore ?? throw new InvalidOperationException("SYSTEM is missing");
        await using var read = (TursoStorageTransaction)await system.BeginTransactionAsync(write: false, cancellationToken);
        var rows = await read.LedgerAsync(command =>
        {
            using var select = command("""
                SELECT COUNT(*) FROM _tb_rebuild
                WHERE organization = $organization AND project = $project AND database = $database AND phase = $phase
                    AND source_machine IS NULL AND source_incarnation IS NULL AND source_head IS NULL;
                """);
            RebuildLedger.Key.Of(fixture.Database).Bind(select);
            select.Parameters.AddWithValue("$phase", (long)RebuildPhase.Pending);
            return Convert.ToInt64(select.ExecuteScalar(), CultureInfo.InvariantCulture);
        }, cancellationToken);
        await Assert.That(rows).IsEqualTo(1).Because("the durable SYSTEM row, not only the refusing cache, must discard its refill");
    }

    private static async Task AssertSideKeptAsync(Fixture fixture, CancellationToken cancellationToken)
    {
        await Assert.That(fixture.Storage.SideExists(fixture.Side)).IsTrue();
        await Assert.That((await File.ReadAllBytesAsync(fixture.Side, cancellationToken)).SequenceEqual(fixture.SideBytes)).IsTrue();
        foreach (var (suffix, bytes) in fixture.Sidecars)
        {
            await Assert.That((await File.ReadAllBytesAsync(fixture.Side + suffix, cancellationToken)).SequenceEqual(bytes)).IsTrue();
        }
    }

    private static Task<ManagementApi> StartHostAsync(string root, Action<IServiceCollection>? configure = null) =>
        ManagementApi.StartAsync(settings => settings["data-directory"] = root, services =>
        {
            services.AddSingleton<IReplicaRebuild, BusyRebuild>();
            configure?.Invoke(services);
        }, time: new FakeTimeProvider());

    private sealed class Fixture : IAsyncDisposable
    {
        private static readonly string[] SidecarSuffixes = ["-wal", "-shm", "-journal"];
        private ManagementApi? _api;
        private TempDirectory? _directory;
        private string _root = string.Empty;
        public TenantContext Database { get; private set; } = null!;
        public Ulid Original { get; private set; }
        public Ulid Rebuilt { get; private set; }
        public Ulid Source { get; } = Ulid.NewUlid();
        public TursoDatabaseStore Copy { get; private set; } = null!;
        public string Side { get; private set; } = string.Empty;
        public byte[] SideBytes { get; private set; } = [];
        public Dictionary<string, byte[]> Sidecars { get; } = [];
        public Action<string, TenantContext>? Checkpoint { get; set; }
        private IServiceProvider Services => (_api ?? throw new InvalidOperationException("the fixture is stopped")).Server.Services;
        public TursoStorageEngine Engine => Services.GetRequiredService<TursoStorageEngine>();
        public IRebuildLedger Ledger => Services.GetRequiredService<IRebuildLedger>();
        public IRebuildRepair Repair => Services.GetRequiredService<IRebuildRepair>();
        public IRebuildStorage Storage => Services.GetRequiredService<IRebuildStorage>();
        public IReplicaCopyFaults Faults => Services.GetRequiredService<IReplicaCopyFaults>();
        public T Service<T>() where T : notnull => Services.GetRequiredService<T>();
        public Uri PeerAddress => (_api ?? throw new InvalidOperationException("the fixture is stopped")).Server.BaseAddress;
        public HttpClient Client => (_api ?? throw new InvalidOperationException("the fixture is stopped")).Client;

        public static async Task<Fixture> StartAsync(bool salvaged, string? root = null)
        {
            var fixture = new Fixture();
            if (root is null)
            {
                fixture._directory = new TempDirectory();
                root = fixture._directory.Root;
            }
            fixture._root = root;
            try
            {
                fixture._api = await fixture.OpenAsync();
                var managed = await fixture._api.CreateManagedDatabaseAsync();
                fixture.Database = await fixture.Services.GetRequiredService<ITenantDirectory>()
                    .FindDatabaseAsync(managed.Organization, managed.Project, managed.Name, CancellationToken.None)
                    is TenantContext found ? found : throw new InvalidOperationException("the managed test database is missing");
                var original = (TursoDatabaseStore)(await fixture.Engine.OpenDatabaseAsync(fixture.Database, CancellationToken.None)
                    ?? throw new InvalidOperationException("the original test copy is missing"));
                await using (var write = await original.BeginDurableTransactionAsync(CancellationToken.None))
                {
                    _ = await write.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
                    _ = await write.CreateCollectionAsync(new CollectionInfo("damaged", 1), CancellationToken.None);
                    var json = System.Text.Encoding.UTF8.GetBytes($"{{\"n\":{new Faker().Random.Int(1, 1000)}}}");
                    _ = await write.PutAsync("items", "kept", json, CancellationToken.None);
                    _ = await write.PutAsync("damaged", "kept", json, CancellationToken.None);
                    _ = await write.CommitAsync(CancellationToken.None);
                }
                fixture.Original = original.Incarnation;
                await fixture.Faults.RecordAsync(fixture.Database, fixture.Original, "the fixture's original fault", CancellationToken.None);
                fixture.Side = fixture.Storage.SidePath(fixture.Database, fixture.Original);
                fixture.Rebuilt = await fixture.Storage.SwapAsync(fixture.Database, fixture.Original, fixture.Side, CancellationToken.None)
                    ?? throw new InvalidOperationException("the fixture did not swap");
                var pending = await fixture.Ledger.GetAsync(fixture.Database, CancellationToken.None)
                    ?? throw new InvalidOperationException("the original fault has no ledger row");
                var swapped = pending with { Phase = RebuildPhase.Swapped, NewIncarnation = fixture.Rebuilt };
                await fixture.Ledger.SaveAsync(swapped, CancellationToken.None);
                var working = await fixture.Storage.CopyForSalvageAsync(fixture.Side, CancellationToken.None);
                try
                {
                    var rows = await fixture.Storage.MeasureSideAsync(working, string.Empty, string.Empty, 1024, 2 * 1024 * 1024, CancellationToken.None);
                    var states = await fixture.Storage.ReadSideAsync(working, rows, CancellationToken.None);
                    var repaired = await fixture.Repair.RepairRebuildAsync(fixture.Database, fixture.Rebuilt, states, CancellationToken.None)
                        ?? throw new InvalidOperationException("the fixture salvage was refused");
                    await Assert.That(repaired).IsGreaterThan(0);
                }
                finally
                {
                    await fixture.Storage.DiscardWorkingCopyAsync(working, CancellationToken.None);
                }
                if (salvaged)
                {
                    await Assert.That(await fixture.Repair.RecordRefillAsync(fixture.Database, fixture.Rebuilt, "cert-ledger-source", fixture.Source, 1, default, CancellationToken.None)).IsTrue();
                    await fixture.Ledger.SaveAsync(swapped with { Phase = RebuildPhase.Salvaged }, CancellationToken.None);
                }
                fixture.Copy = (TursoDatabaseStore)(await fixture.Engine.OpenDatabaseAsync(fixture.Database, CancellationToken.None))!;
                fixture.SideBytes = await File.ReadAllBytesAsync(fixture.Side);
                foreach (var suffix in SidecarSuffixes.Where(suffix => File.Exists(fixture.Side + suffix)))
                {
                    fixture.Sidecars.Add(suffix, await File.ReadAllBytesAsync(fixture.Side + suffix));
                }
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        public async Task<TursoDatabaseStore> CreateHealthyCopyAsync(CancellationToken cancellationToken)
        {
            var managed = await (_api ?? throw new InvalidOperationException("the fixture is stopped")).CreateManagedDatabaseAsync();
            var database = await Services.GetRequiredService<ITenantDirectory>()
                .FindDatabaseAsync(managed.Organization, managed.Project, managed.Name, cancellationToken)
                is TenantContext found ? found : throw new InvalidOperationException("the healthy test database is missing");
            return (TursoDatabaseStore)(await Engine.OpenDatabaseAsync(database, cancellationToken)
                ?? throw new InvalidOperationException("the healthy test copy is missing"));
        }

        private Task<ManagementApi> OpenAsync() => StartHostAsync(_root, services =>
            services.AddSingleton<DatabaseLifecycleCheckpoint>((step, database) => Checkpoint?.Invoke(step, database)));

        public async ValueTask DisposeAsync()
        {
            Checkpoint = null;
            try
            {
                if (_api is { } api)
                {
                    _api = null;
                    await api.DisposeAsync();
                    await api.CleanupCompletion;
                }
            }
            finally
            {
                _directory?.Dispose();
                _directory = null;
            }
        }
    }

    private sealed class BusyRebuild : IReplicaRebuild
    {
        public ValueTask<RebuildOutcome> RebuildAsync(TenantContext database, Ulid faultedIncarnation, CancellationToken cancellationToken) =>
            ValueTask.FromResult<RebuildOutcome>(new RebuildOutcome.Refused("the persisted identity test owns this rebuild"));
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("terrabase-persisted-identity-").FullName;
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
