// Draft destination: tests/TerraBase.Fast/Replication/RebuildCertificationLedgerSafetyTests.cs
// Requires the ledger draft and reader.reset checkpoint patch; no production certification DI change.
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Engine.Sqlite;
using TerraBase.Features.Replication;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraBase.Testing;
using Turso.Data.Sqlite;

namespace TerraBase.Fast.Replication;

public sealed class RebuildCertificationLedgerSafetyTests
{
    private const string CrashRoot = "TERRABASE_TEST_CERT_LEDGER_CRASH_ROOT";

    [Test]
    [Arguments(false, RebuildLedger.Publishing)]
    [Arguments(false, RebuildLedger.Published)]
    [Arguments(true, RebuildLedger.BarrierDisposed)]
    [Arguments(true, RebuildLedger.Publishing)]
    [Arguments(true, RebuildLedger.Published)]
    public async Task A_retained_native_damaged_read_cannot_publish_a_proof_or_certificate(bool completing, string injection)
    {
        await using var fixture = await Fixture.StartAsync(salvaged: completing, corruptPage: true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var retained = await fixture.Copy.BeginTransactionAsync(write: false, deadline.Token);
        SqliteException? detected = null;
        var hits = 0;
        fixture.Checkpoint = (step, database) =>
        {
            if (step != injection || database != fixture.Database || Interlocked.Increment(ref hits) != 1)
            {
                return;
            }

            // Native read, not a manually assigned marker. Keep the transaction alive so its durable fault row lags
            // the immediate CopyFault. This checkpoint runs outside tenant command execution; it waits only for a read.
            try
            {
                _ = retained.GetAsync("damaged", "kept", deadline.Token).AsTask().GetAwaiter().GetResult();
            }
            catch (SqliteException error)
            {
                detected = error;
            }
        };
        var accepted = completing
            ? await fixture.Repair.CompleteRebuildAsync(fixture.Database, fixture.Original, fixture.Rebuilt, deadline.Token)
            : await fixture.Repair.RecordRefillAsync(fixture.Database, fixture.Rebuilt, "cert-ledger-source", fixture.Source, 1, deadline.Token);
        fixture.Checkpoint = null;
        await Assert.That(hits).IsEqualTo(1);
        await Assert.That(detected?.SqliteErrorCode).IsEqualTo(11);
        await Assert.That(fixture.Copy.CopyFault).IsNotNull();
        await Assert.That(accepted).IsFalse();
        var current = await fixture.Ledger.GetAsync(fixture.Database, deadline.Token);
        await Assert.That(current?.Phase == RebuildPhase.Certified).IsFalse();
        if (injection != RebuildLedger.Published)
        {
            await Assert.That(current).IsEqualTo(fixture.Expected)
                .Because("the retained read has not disposed to change the durable row; the marker alone must refuse publication");
        }
        else
        {
            await Assert.That(fixture.Copy.RebuildRow?.Refill).IsNull()
                .Because("a known fault during SYSTEM publication must withdraw the newly written proof or certificate");
        }
        await AssertOrdinaryWriteRefusedAsync(fixture.Copy, deadline.Token);
        await AssertSideKeptAsync(fixture, deadline.Token);
        await retained.DisposeAsync();
        await Assert.That((await fixture.Ledger.GetAsync(fixture.Database, deadline.Token))?.Phase).IsEqualTo(RebuildPhase.Pending);
        await Assert.That(await fixture.Repair.CompleteRebuildAsync(fixture.Database, fixture.Original, fixture.Rebuilt, deadline.Token)).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_fault_during_barrier_disposal_precedes_every_certification_attempt(bool systemFault)
    {
        await using var fixture = await Fixture.StartAsync(salvaged: true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var reset = false;
        var published = false;
        List<Task> faultRecordings = [];
        fixture.Checkpoint = (step, database) =>
        {
            if (database != fixture.Database)
            {
                return;
            }
            if (step == RebuildLedger.Published)
            {
                published = true;
            }
            if (step == "writer.reset" && !reset)
            {
                reset = true;
                if (systemFault)
                {
                    faultRecordings.Add(fixture.Faults.RecordAsync(ChangeGroups.System, Ulid.Empty, "a node fault during barrier disposal", CancellationToken.None).AsTask());
                    return;
                }
                throw new SqliteException("an I/O error resetting the committed barrier", 10);
            }
        };
        try
        {
            await Assert.That(await fixture.Repair.CompleteRebuildAsync(fixture.Database, fixture.Original, fixture.Rebuilt, deadline.Token)).IsFalse();
        }
        finally
        {
            fixture.Checkpoint = null;
            await Task.WhenAll(faultRecordings);
        }
        await Assert.That(reset).IsTrue();
        await Assert.That(published).IsFalse().Because("barrier disposal must finish, including its durable fault recording, before SYSTEM certification");
        await Assert.That((await fixture.Ledger.GetAsync(fixture.Database, deadline.Token))?.Phase == RebuildPhase.Certified).IsFalse();
        await AssertOrdinaryWriteRefusedAsync(fixture.Copy, deadline.Token);
        await AssertSideKeptAsync(fixture, deadline.Token);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Damage_resetting_the_publication_read_refuses_the_durable_proof_or_certificate(bool completing)
    {
        await using var fixture = await Fixture.StartAsync(salvaged: completing);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var damage = new SqliteException("a damaged publication read reset", 10);
        var armed = false;
        var publications = 0;
        var resets = 0;
        RebuildLedger.Row? cachedAtReset = null;
        fixture.Checkpoint = (step, database) =>
        {
            if (database != fixture.Database)
            {
                return;
            }
            if (step == RebuildLedger.Published)
            {
                publications++;
                armed = true;
            }
            if (step == "reader.reset" && armed)
            {
                armed = false;
                resets++;
                cachedAtReset = fixture.Copy.RebuildRow;
                throw damage;
            }
        };
        SqliteException? error;
        try
        {
            error = await Assert.ThrowsAsync<SqliteException>(async () =>
            {
                var publication = completing
                    ? fixture.Repair.CompleteRebuildAsync(fixture.Database, fixture.Original, fixture.Rebuilt, deadline.Token).AsTask()
                    : fixture.Repair.RecordRefillAsync(fixture.Database, fixture.Rebuilt, "cert-ledger-source", fixture.Source, 1, deadline.Token).AsTask();
                // Cleanup records damage with CancellationToken.None; a gate/disposal deadlock must still time out.
                _ = await publication.WaitAsync(TimeSpan.FromSeconds(30));
            });
        }
        finally
        {
            fixture.Checkpoint = null;
        }
        await Assert.That(ReferenceEquals(error, damage)).IsTrue();
        await Assert.That(error!.SqliteErrorCode).IsEqualTo(10);
        await Assert.That(publications).IsEqualTo(1);
        await Assert.That(resets).IsEqualTo(1);
        await Assert.That(cachedAtReset?.Phase).IsEqualTo(fixture.Expected.Phase);
        await Assert.That(cachedAtReset?.Refill).IsEqualTo(fixture.ExpectedRefill)
            .Because("the verification read must dispose before its publication grants cached permission");
        await Assert.That(fixture.Copy.CopyFault).IsNotNull();
        var pending = await fixture.Ledger.GetAsync(fixture.Database, deadline.Token);
        await Assert.That(pending?.Phase).IsEqualTo(RebuildPhase.Pending);
        await Assert.That(pending?.NewIncarnation).IsNull();
        // Disposal reads the already-published row: Certified faults begin a rebuild of the rebuilt incarnation.
        await Assert.That(pending?.OldIncarnation).IsEqualTo(completing ? fixture.Rebuilt : fixture.Original);
        await Assert.That(pending?.AdditionalIncarnation).IsEqualTo(completing ? fixture.Original : fixture.Rebuilt);
        await AssertDurablePendingWithoutRefillAsync(fixture, deadline.Token);
        await Assert.That(fixture.Copy.RebuildRow?.Phase).IsEqualTo(RebuildPhase.Pending);
        await Assert.That(fixture.Copy.RebuildRow?.Refill).IsNull();
        await AssertOrdinaryWriteRefusedAsync(fixture.Copy, deadline.Token);
        await Assert.That(await fixture.Repair.RecordRefillAsync(fixture.Database, fixture.Rebuilt, "cert-ledger-source", fixture.Source, 1, deadline.Token)).IsFalse();
        await Assert.That(await fixture.Repair.CompleteRebuildAsync(fixture.Database, fixture.Original, fixture.Rebuilt, deadline.Token)).IsFalse();
        await Assert.That(await fixture.Storage.FinalizeAsync(fixture.Database, fixture.Rebuilt, fixture.Side, deadline.Token)).IsFalse();
        await Assert.That(fixture.Engine.FileExists(fixture.Database)).IsTrue();
        await AssertSideKeptAsync(fixture, deadline.Token);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task Certified_retry_refuses_node_or_copy_damage_before_read_or_after_read_disposal(bool afterDisposal, bool systemFault)
    {
        await using var fixture = await Fixture.StartAsync(salvaged: true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await Assert.That(await fixture.Repair.CompleteRebuildAsync(fixture.Database, fixture.Original, fixture.Rebuilt, deadline.Token)).IsTrue();
        var certified = await fixture.Ledger.GetAsync(fixture.Database, deadline.Token);
        var hits = 0;
        List<Task> faultRecordings = [];
        void MarkFault()
        {
            if (systemFault)
            {
                faultRecordings.Add(fixture.Faults.RecordAsync(ChangeGroups.System, Ulid.Empty, "a node fault on certified retry", CancellationToken.None).AsTask());
            }
            else
            {
                // Isolate the synchronous production marker without moving the certificate row: row equality alone cannot pass.
                fixture.Copy.NoteCopyFault("a published copy fault on certified retry");
            }
        }
        if (afterDisposal)
        {
            fixture.Checkpoint = (step, database) =>
            {
                if (step == RebuildLedger.BarrierReadDisposed && database == fixture.Database)
                {
                    hits++;
                    MarkFault();
                }
            };
        }
        else
        {
            MarkFault();
        }
        try
        {
            await Assert.That(await fixture.Repair.CompleteRebuildAsync(fixture.Database, fixture.Original, fixture.Rebuilt, deadline.Token)).IsFalse();
        }
        finally
        {
            fixture.Checkpoint = null;
            await Task.WhenAll(faultRecordings);
        }
        await Assert.That(hits).IsEqualTo(afterDisposal ? 1 : 0);
        await Assert.That(await fixture.Ledger.GetAsync(fixture.Database, deadline.Token)).IsEqualTo(certified);
        await AssertOrdinaryWriteRefusedAsync(fixture.Copy, deadline.Token);
        await Assert.That(await fixture.Storage.FinalizeAsync(fixture.Database, fixture.Rebuilt, fixture.Side, deadline.Token)).IsFalse();
        await AssertSideKeptAsync(fixture, deadline.Token);
    }

    [Test]
    [Arguments("refill")]
    [Arguments("complete")]
    [Arguments("certified")]
    public async Task Persisted_receiver_identity_is_checked_even_when_the_cached_identity_matches(string operation)
    {
        await using var fixture = await Fixture.StartAsync(salvaged: operation != "refill");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        if (operation == "certified")
        {
            await Assert.That(await fixture.Repair.CompleteRebuildAsync(fixture.Database, fixture.Original, fixture.Rebuilt, deadline.Token)).IsTrue();
        }
        var changed = Ulid.NewUlid();
        _ = await fixture.Copy.WithWriterAsync(session =>
        {
            using var command = session.Connection.CreateCommand();
            command.CommandText = SqliteStatements.RenewIncarnation;
            command.Parameters.AddWithValue("$incarnation", changed.ToString());
            return command.ExecuteNonQuery();
        }, "a test replacing persisted receiver identity", deadline.Token);
        await Assert.That(fixture.Copy.Incarnation).IsEqualTo(fixture.Rebuilt).Because("the open store deliberately still caches the old identity");
        var accepted = operation == "refill"
            ? await fixture.Repair.RecordRefillAsync(fixture.Database, fixture.Rebuilt, "cert-ledger-source", fixture.Source, 1, deadline.Token)
            : await fixture.Repair.CompleteRebuildAsync(fixture.Database, fixture.Original, fixture.Rebuilt, deadline.Token);
        await Assert.That(accepted).IsFalse();
        await Assert.That(await fixture.Storage.FinalizeAsync(fixture.Database, fixture.Rebuilt, fixture.Side, deadline.Token)).IsFalse();
        await AssertSideKeptAsync(fixture, deadline.Token);
    }

    [Test]
    [Arguments(false, RebuildLedger.Publishing)]
    [Arguments(false, RebuildLedger.Published)]
    [Arguments(true, RebuildLedger.Publishing)]
    [Arguments(true, RebuildLedger.Published)]
    public async Task SYSTEM_damage_during_publication_refuses_success_and_keeps_the_cache_refusing(bool completing, string injection)
    {
        await using var fixture = await Fixture.StartAsync(salvaged: completing);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var hits = 0;
        List<Task> faultRecordings = [];
        fixture.Checkpoint = (step, database) =>
        {
            if (step == injection && database == fixture.Database)
            {
                hits++;
                faultRecordings.Add(fixture.Faults.RecordAsync(ChangeGroups.System, Ulid.Empty, "a SYSTEM fault during proof publication", CancellationToken.None).AsTask());
            }
        };
        bool accepted;
        try
        {
            accepted = completing
                ? await fixture.Repair.CompleteRebuildAsync(fixture.Database, fixture.Original, fixture.Rebuilt, deadline.Token)
                : await fixture.Repair.RecordRefillAsync(fixture.Database, fixture.Rebuilt, "cert-ledger-source", fixture.Source, 1, deadline.Token);
        }
        finally
        {
            fixture.Checkpoint = null;
            await Task.WhenAll(faultRecordings);
        }
        await Assert.That(hits).IsEqualTo(1);
        await Assert.That(accepted).IsFalse();
        await Assert.That(fixture.Copy.RebuildRow?.Phase).IsEqualTo(fixture.Expected.Phase);
        await Assert.That(fixture.Copy.RebuildRow?.Refill).IsEqualTo(fixture.ExpectedRefill);
        await Assert.That(await fixture.Engine.IsReadyAsync(deadline.Token)).IsFalse();
        await AssertOrdinaryWriteRefusedAsync(fixture.Copy, deadline.Token);
        await AssertSideKeptAsync(fixture, deadline.Token);
    }

    [Test]
    public async Task A_database_deleted_and_replaced_after_barrier_disposal_cannot_receive_the_stale_certificate()
    {
        await using var fixture = await Fixture.StartAsync(salvaged: true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        SqliteDatabaseStore? replacement = null;
        var hits = 0;
        fixture.Checkpoint = (step, database) =>
        {
            if (step != RebuildLedger.BarrierDisposed || database != fixture.Database)
            {
                return;
            }
            hits++;
            // This gap holds neither the ledger gate nor the tenant writer, so the real lifecycle operations can finish.
            _ = fixture.Engine.DeleteDatabaseAsync(fixture.Database, deadline.Token).AsTask().GetAwaiter().GetResult();
            if (!fixture.Engine.CreateDatabaseAsync(fixture.Database, deadline.Token).AsTask().GetAwaiter().GetResult())
            {
                throw new InvalidOperationException("the deleted database was not recreated");
            }
            replacement = (SqliteDatabaseStore)(fixture.Engine.OpenDatabaseAsync(fixture.Database, deadline.Token).AsTask().GetAwaiter().GetResult()
                ?? throw new InvalidOperationException("the replacement did not open"));
        };
        await Assert.That(await fixture.Repair.CompleteRebuildAsync(fixture.Database, fixture.Original, fixture.Rebuilt, deadline.Token)).IsFalse();
        fixture.Checkpoint = null;
        await Assert.That(hits).IsEqualTo(1);
        await Assert.That(replacement).IsNotNull();
        await Assert.That(replacement!.Incarnation).IsNotEqualTo(fixture.Rebuilt);
        await Assert.That(await fixture.Ledger.GetAsync(fixture.Database, deadline.Token)).IsNull();
        await Assert.That(replacement.RebuildRow).IsNull();
        await using var write = await replacement.BeginDurableTransactionAsync(deadline.Token);
        _ = await write.CreateCollectionAsync(new CollectionInfo("replacement", 1), deadline.Token);
        _ = await write.CommitAsync(deadline.Token);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task Cancellation_after_SYSTEM_publication_withdraws_it_and_a_healthy_retry_can_finish(bool completing, bool withdrawalFails)
    {
        await using var fixture = await Fixture.StartAsync(salvaged: completing);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var system = fixture.Engine.SystemStore ?? throw new InvalidOperationException("SYSTEM is missing");
        var previous = system.AfterSqliteCommit;
        var failure = new IOException("the withdrawal follow-up failed");
        SqliteDatabaseStore? healthy = null;
        if (withdrawalFails)
        {
            healthy = await fixture.CreateHealthyCopyAsync(deadline.Token);
            await Assert.That(healthy.CopyFault).IsNull();
            await Assert.That(healthy.RebuildRow).IsNull();
            await using var healthyWrite = await healthy.BeginDurableTransactionAsync(deadline.Token);
            _ = await healthyWrite.CreateCollectionAsync(new CollectionInfo("healthy", 1), deadline.Token);
            _ = await healthyWrite.CommitAsync(deadline.Token);
        }
        var hits = 0;
        var withdrawals = 0;
        fixture.Checkpoint = (step, database) =>
        {
            if (step == RebuildLedger.Published && database == fixture.Database)
            {
                hits++;
                if (withdrawalFails)
                {
                    // Installed only after publication committed: this is the withdrawal's actual SYSTEM transaction.
                    system.AfterSqliteCommit = () =>
                    {
                        withdrawals++;
                        throw failure;
                    };
                }
                caller.Cancel();
            }
        };
        async Task PublishAsync()
        {
            var publication = completing
                ? fixture.Repair.CompleteRebuildAsync(fixture.Database, fixture.Original, fixture.Rebuilt, caller.Token).AsTask()
                : fixture.Repair.RecordRefillAsync(fixture.Database, fixture.Rebuilt, "cert-ledger-source", fixture.Source, 1, caller.Token).AsTask();
            _ = await publication.WaitAsync(TimeSpan.FromSeconds(30));
        }
        try
        {
            if (withdrawalFails)
            {
                var error = await Assert.ThrowsAsync<IOException>(PublishAsync);
                await Assert.That(ReferenceEquals(error, failure)).IsTrue();
            }
            else
            {
                _ = await Assert.ThrowsAsync<OperationCanceledException>(PublishAsync);
            }
        }
        finally
        {
            system.AfterSqliteCommit = previous;
            fixture.Checkpoint = null;
        }
        await Assert.That(hits).IsEqualTo(1);
        await Assert.That(withdrawals).IsEqualTo(withdrawalFails ? 1 : 0);
        await Assert.That(await fixture.Ledger.GetAsync(fixture.Database, deadline.Token)).IsEqualTo(fixture.Expected);
        await Assert.That(fixture.Copy.RebuildRow?.Refill).IsEqualTo(fixture.ExpectedRefill);
        await AssertOrdinaryWriteRefusedAsync(fixture.Copy, deadline.Token);
        if (withdrawalFails)
        {
            await Assert.That(((RebuildLedger)fixture.Ledger).FailedClosed).Contains("a rebuild publication could not be withdrawn");
            await Assert.That(await fixture.Engine.IsReadyAsync(deadline.Token)).IsFalse();
            await Assert.That(await fixture.Faults.RefusedAsync(ChangeGroups.System, Ulid.Empty, deadline.Token)).IsTrue();
            await Assert.That(system.CopyFault).IsNull();
            await Assert.That(healthy!.CopyFault).IsNull();
            await AssertOrdinaryWriteRefusedAsync(healthy!, deadline.Token);
            await Assert.That(await fixture.Repair.CompleteRebuildAsync(fixture.Database, fixture.Original, fixture.Rebuilt, deadline.Token)).IsFalse();
            await Assert.That(await fixture.Storage.FinalizeAsync(fixture.Database, fixture.Rebuilt, fixture.Side, deadline.Token)).IsFalse();
            await AssertSideKeptAsync(fixture, deadline.Token);
            return;
        }
        if (!completing)
        {
            await Assert.That(await fixture.Repair.RecordRefillAsync(fixture.Database, fixture.Rebuilt, "cert-ledger-source", fixture.Source, 1, deadline.Token)).IsTrue();
            await fixture.Ledger.SaveAsync(fixture.Expected with { Phase = RebuildPhase.Salvaged }, deadline.Token);
        }
        RebuildPhase? cachedAtPublication = null;
        fixture.Checkpoint = (step, database) =>
        {
            if (step == RebuildLedger.Published && database == fixture.Database)
            {
                cachedAtPublication = fixture.Copy.RebuildRow?.Phase;
            }
        };
        await Assert.That(await fixture.Repair.CompleteRebuildAsync(fixture.Database, fixture.Original, fixture.Rebuilt, deadline.Token)).IsTrue();
        fixture.Checkpoint = null;
        await Assert.That(cachedAtPublication).IsEqualTo(RebuildPhase.Salvaged)
            .Because("the cache must still refuse ordinary writes while publication reads remain undisposed");
        await Assert.That(await fixture.Repair.CompleteRebuildAsync(fixture.Database, fixture.Original, fixture.Rebuilt, deadline.Token)).IsTrue();
        await using var write = await fixture.Copy.BeginDurableTransactionAsync(deadline.Token);
        _ = await write.PutAsync("items", "after-certificate", "{\"n\":2}"u8.ToArray(), deadline.Token);
        _ = await write.CommitAsync(deadline.Token);
    }

    [Test]
    public async Task A_crash_after_the_disposed_FULL_barrier_keeps_the_side_copy_and_refuses_on_restart()
    {
        if (Environment.GetEnvironmentVariable(CrashRoot) is { } childRoot)
        {
            await using var fixture = await Fixture.StartAsync(salvaged: true, root: Path.Combine(childRoot, "data"));
            await File.WriteAllLinesAsync(Path.Combine(childRoot, "barrier-crash.txt"),
            [
                fixture.Database.OrganizationId, fixture.Database.ProjectId!, fixture.Database.DatabaseId!,
                fixture.Original.ToString(), fixture.Rebuilt.ToString(), Convert.ToHexString(SHA256.HashData(fixture.SideBytes)),
            ]);
            fixture.Checkpoint = (step, database) =>
            {
                if (step == RebuildLedger.BarrierDisposed && database == fixture.Database)
                {
                    Environment.FailFast("crashed after disposed FULL rebuild barrier");
                }
            };
            _ = await fixture.Repair.CompleteRebuildAsync(fixture.Database, fixture.Original, fixture.Rebuilt, CancellationToken.None);
            throw new InvalidOperationException("the child outlived the barrier crash checkpoint");
        }

        using var directory = new TempDirectory();
        var (exit, output) = await RunCrashChildAsync(directory.Root);
        await Assert.That(exit).IsNotEqualTo(0).Because(output);
        await Assert.That(output).Contains("crashed after disposed FULL rebuild barrier");
        var facts = await File.ReadAllLinesAsync(Path.Combine(directory.Root, "barrier-crash.txt"));
        var database = new TenantContext(facts[0], PlanTier.Hobby, facts[1], facts[2]);
        var original = Ulid.Parse(facts[3]);
        var rebuilt = Ulid.Parse(facts[4]);
        await using var api = await StartHostAsync(Path.Combine(directory.Root, "data"));
        var services = api.Server.Services;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var pending = await services.GetRequiredService<IRebuildLedger>().GetAsync(database, deadline.Token);
        await Assert.That(pending?.Phase).IsEqualTo(RebuildPhase.Pending);
        await Assert.That(pending?.OldIncarnation).IsEqualTo(original);
        await Assert.That(pending?.AdditionalIncarnation).IsEqualTo(rebuilt);
        var copy = (SqliteDatabaseStore)(await services.GetRequiredService<SqliteStorageEngine>().OpenDatabaseAsync(database, deadline.Token)
            ?? throw new InvalidOperationException("the barrier's copy is missing"));
        await Assert.That(copy.RebuildRow?.Refill).IsNull();
        await AssertOrdinaryWriteRefusedAsync(copy, deadline.Token);
        await Assert.That(await services.GetRequiredService<IRebuildRepair>().CompleteRebuildAsync(database, original, rebuilt, deadline.Token)).IsFalse();
        var side = services.GetRequiredService<IRebuildStorage>().SidePath(database, original);
        await Assert.That(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(side, deadline.Token)))).IsEqualTo(facts[5]);
    }

    private static async Task AssertOrdinaryWriteRefusedAsync(SqliteDatabaseStore copy, CancellationToken cancellationToken)
    {
        _ = await Assert.ThrowsAsync<ReplicaCopyFaultedException>(async () =>
        {
            await using var write = await copy.BeginDurableTransactionAsync(cancellationToken);
        });
    }

    private static async Task AssertDurablePendingWithoutRefillAsync(Fixture fixture, CancellationToken cancellationToken)
    {
        var system = fixture.Engine.SystemStore ?? throw new InvalidOperationException("SYSTEM is missing");
        await using var read = (SqliteStorageTransaction)await system.BeginTransactionAsync(write: false, cancellationToken);
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
        public SqliteDatabaseStore Copy { get; private set; } = null!;
        public RebuildRecord Expected { get; private set; } = null!;
        public RebuildLedger.Refill? ExpectedRefill { get; private set; }
        public string Side { get; private set; } = string.Empty;
        public byte[] SideBytes { get; private set; } = [];
        public Dictionary<string, byte[]> Sidecars { get; } = [];
        public Action<string, TenantContext>? Checkpoint { get; set; }
        private IServiceProvider Services => (_api ?? throw new InvalidOperationException("the fixture is stopped")).Server.Services;
        public SqliteStorageEngine Engine => Services.GetRequiredService<SqliteStorageEngine>();
        public IRebuildLedger Ledger => Services.GetRequiredService<IRebuildLedger>();
        public IRebuildRepair Repair => Services.GetRequiredService<IRebuildRepair>();
        public IRebuildStorage Storage => Services.GetRequiredService<IRebuildStorage>();
        public IReplicaCopyFaults Faults => Services.GetRequiredService<IReplicaCopyFaults>();

        public static async Task<Fixture> StartAsync(bool salvaged, bool corruptPage = false, string? root = null)
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
                var original = (SqliteDatabaseStore)(await fixture.Engine.OpenDatabaseAsync(fixture.Database, CancellationToken.None)
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
                    await Assert.That(await fixture.Repair.RecordRefillAsync(fixture.Database, fixture.Rebuilt, "cert-ledger-source", fixture.Source, 1, CancellationToken.None)).IsTrue();
                    await fixture.Ledger.SaveAsync(swapped with { Phase = RebuildPhase.Salvaged }, CancellationToken.None);
                }
                fixture.Expected = (await fixture.Ledger.GetAsync(fixture.Database, CancellationToken.None))!;
                fixture.Copy = (SqliteDatabaseStore)(await fixture.Engine.OpenDatabaseAsync(fixture.Database, CancellationToken.None))!;
                fixture.ExpectedRefill = fixture.Copy.RebuildRow?.Refill;
                fixture.SideBytes = await File.ReadAllBytesAsync(fixture.Side);
                foreach (var suffix in SidecarSuffixes.Where(suffix => File.Exists(fixture.Side + suffix)))
                {
                    fixture.Sidecars.Add(suffix, await File.ReadAllBytesAsync(fixture.Side + suffix));
                }
                if (corruptPage)
                {
                    var path = fixture.Copy.FilePath;
                    await fixture._api.DisposeAsync();
                    await fixture._api.CleanupCompletion;
                    fixture._api = null;
                    await CorruptCollectionPageAsync(path);
                    fixture._api = await fixture.OpenAsync();
                    fixture.Copy = (SqliteDatabaseStore)(await fixture.Engine.OpenDatabaseAsync(fixture.Database, CancellationToken.None))!;
                    await Assert.That(await fixture.Ledger.GetAsync(fixture.Database, CancellationToken.None)).IsEqualTo(fixture.Expected);
                }
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        public async Task<SqliteDatabaseStore> CreateHealthyCopyAsync(CancellationToken cancellationToken)
        {
            var managed = await (_api ?? throw new InvalidOperationException("the fixture is stopped")).CreateManagedDatabaseAsync();
            var database = await Services.GetRequiredService<ITenantDirectory>()
                .FindDatabaseAsync(managed.Organization, managed.Project, managed.Name, cancellationToken)
                is TenantContext found ? found : throw new InvalidOperationException("the healthy test database is missing");
            return (SqliteDatabaseStore)(await Engine.OpenDatabaseAsync(database, cancellationToken)
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

    private static async Task CorruptCollectionPageAsync(string path)
    {
        long offset;
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString()))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            _ = await command.ExecuteNonQueryAsync();
            command.CommandText = "PRAGMA page_size;";
            var size = Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
            command.CommandText = "SELECT rootpage FROM sqlite_schema WHERE name = $table;";
            command.Parameters.AddWithValue("$table", SqliteStorageTransaction.Table("damaged").Trim('"'));
            var page = Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
            await Assert.That(page).IsGreaterThan(1);
            offset = (page - 1) * size;
        }
        using var file = File.OpenWrite(path);
        file.Position = offset;
        file.WriteByte(0xFF);
    }

    private static async Task<(int ExitCode, string Output)> RunCrashChildAsync(string root)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("the test executable is unavailable");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add(Path.GetFullPath(Environment.GetCommandLineArgs()[0]));
        }
        var type = typeof(RebuildCertificationLedgerSafetyTests);
        foreach (var argument in new[]
        {
            "--treenode-filter", $"/{type.Assembly.GetName().Name}/{type.Namespace}/{type.Name}/{nameof(A_crash_after_the_disposed_FULL_barrier_keeps_the_side_copy_and_refuses_on_restart)}",
            "--no-progress", "--disable-logo", "--exit-on-process-exit", Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
            "--results-directory", Directory.CreateDirectory(Path.Combine(root, "results")).FullName,
        })
        {
            start.ArgumentList.Add(argument);
        }
        start.Environment[CrashRoot] = root;
        start.Environment["TMPDIR"] = Directory.CreateDirectory(Path.Combine(root, "tmp")).FullName;
        using var child = Process.Start(start) ?? throw new InvalidOperationException("the barrier crash child did not start");
        var output = child.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var error = child.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await child.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromMinutes(2));
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync(CancellationToken.None);
            }
        }
        var text = await output + await error;
        return (child.ExitCode, text.Length > 16384 ? text[^16384..] : text);
    }

    private sealed class BusyRebuild : IReplicaRebuild
    {
        public ValueTask<RebuildOutcome> RebuildAsync(TenantContext database, Ulid faultedIncarnation, CancellationToken cancellationToken) =>
            ValueTask.FromResult<RebuildOutcome>(new RebuildOutcome.Refused("the certification ledger test owns this rebuild"));
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("terrabase-cert-ledger-safety-").FullName;
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
