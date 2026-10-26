using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Features.Replication;
using TerraBase.Features.Replication.V1;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraBase.Testing;
using Turso.Data.Sqlite;

namespace TerraBase.Fast.Replication;

public sealed class RebuildLedgerRecoveryTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_fault_observed_after_native_commit_cannot_return_a_successful_marker(bool systemFault)
    {
        var root = Directory.CreateTempSubdirectory("terrabase-commit-marker-fault-").FullName;
        try
        {
            Action<TenantContext>? atMarker = null;
            await using var api = await ManagementApi.StartAsync(settings => settings["data-directory"] = root, services =>
            {
                services.AddSingleton<IReplicaRebuild, BusyRebuild>();
                services.AddSingleton<DatabaseLifecycleCheckpoint>((step, database) =>
                {
                    if (step == "writer.marker")
                    {
                        atMarker?.Invoke(database);
                    }
                });
            });
            var managed = await api.CreateManagedDatabaseAsync();
            var services = api.Server.Services;
            var database = await services.GetRequiredService<ITenantDirectory>()
                .FindDatabaseAsync(managed.Organization, managed.Project, managed.Name, CancellationToken.None)
                is TenantContext found ? found : throw new InvalidOperationException("the test database is not registered");
            var store = await services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(database, CancellationToken.None)
                ?? throw new InvalidOperationException("the test database is missing");
            await using (var setup = await store.BeginDurableTransactionAsync(CancellationToken.None))
            {
                _ = await setup.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
                _ = await setup.CommitAsync(CancellationToken.None);
            }

            // Admit before the injected fault; first SELECT still observes the completed commit or rollback.
            await using var read = await store.BeginTransactionAsync(write: false, CancellationToken.None);

            var faults = services.GetRequiredService<IReplicaCopyFaults>();
            Task? recording = null;
            atMarker = marked =>
            {
                if (marked == database)
                {
                    // The production record seam marks the exact copy synchronously, before its durable SYSTEM await.
                    recording = faults.RecordAsync(systemFault ? ChangeGroups.System : database,
                        systemFault ? Ulid.Empty : store.Incarnation, "a test fault after native COMMIT", CancellationToken.None).AsTask();
                }
            };
            await using (var write = await store.BeginDurableTransactionAsync(CancellationToken.None))
            {
                _ = await write.PutAsync("items", "landed", "{\"n\":2}"u8.ToArray(), CancellationToken.None);
                _ = await Assert.ThrowsAsync<ReplicaCopyFaultedException>(async () => await write.CommitAsync(CancellationToken.None));
            }

            atMarker = null;
            await Assert.That(recording).IsNotNull().Because("the fault was observed after native COMMIT, while reading its marker");
            await recording!;
            await Assert.That(await faults.RefusedAsync(database, store.Incarnation, CancellationToken.None)).IsTrue();
            await Assert.That(await read.GetAsync("items", "landed", CancellationToken.None)).IsNotNull()
                .Because("the commit landed, but its damaged copy returned no successful acknowledgement");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task A_held_tenant_writer_cannot_commit_after_a_SYSTEM_fault()
    {
        var root = Directory.CreateTempSubdirectory("terrabase-held-node-fault-").FullName;
        try
        {
            await using var api = await StartAsync(root);
            var managed = await api.CreateManagedDatabaseAsync();
            var services = api.Server.Services;
            var database = await services.GetRequiredService<ITenantDirectory>()
                .FindDatabaseAsync(managed.Organization, managed.Project, managed.Name, CancellationToken.None)
                is TenantContext found ? found : throw new InvalidOperationException("the test database is not registered");
            var store = await services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(database, CancellationToken.None)
                ?? throw new InvalidOperationException("the test database is missing");
            await using (var setup = await store.BeginDurableTransactionAsync(CancellationToken.None))
            {
                _ = await setup.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
                _ = await setup.PutAsync("items", "kept", "{\"n\":1}"u8.ToArray(), CancellationToken.None);
                _ = await setup.CommitAsync(CancellationToken.None);
            }

            var system = await services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None);
            var incarnation = system.Incarnation;
            var rebuild = ActivatorUtilities.CreateInstance<ReplicaRebuild>(services);
            await Assert.That(await rebuild.RebuildAsync(ChangeGroups.System, incarnation, CancellationToken.None))
                .IsEqualTo(new RebuildOutcome.Refused("the system database cannot be rebuilt in process"));
            await Assert.That((await services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None)).Incarnation)
                .IsEqualTo(incarnation);

            // Admit before the injected fault; first SELECT still observes the completed commit or rollback.
            await using var read = await store.BeginTransactionAsync(write: false, CancellationToken.None);

            var sequence = store.CommitSequence;
            await using (var held = await store.BeginDurableTransactionAsync(CancellationToken.None))
            {
                _ = await held.PutAsync("items", "refused", "{\"n\":2}"u8.ToArray(), CancellationToken.None);
                await services.GetRequiredService<IReplicaCopyFaults>()
                    .RecordAsync(ChangeGroups.System, Ulid.Empty, "a test SYSTEM fault", CancellationToken.None);
                _ = await Assert.ThrowsAsync<ReplicaCopyFaultedException>(async () => await held.CommitAsync(CancellationToken.None));
            }

            await Assert.That(store.CommitSequence).IsEqualTo(sequence);
            await Assert.That(await read.GetAsync("items", "refused", CancellationToken.None)).IsNull();
            await Assert.That(await read.GetAsync("items", "kept", CancellationToken.None)).IsNotNull();
            _ = await Assert.ThrowsAsync<ReplicaCopyFaultedException>(async () =>
            {
                await using var refused = await store.BeginDurableTransactionAsync(CancellationToken.None);
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task A_corrupt_SYSTEM_read_refuses_the_node_and_peers_before_its_transaction_is_released()
    {
        var root = Directory.CreateTempSubdirectory("terrabase-system-page-fault-").FullName;
        try
        {
            TenantContext database;
            await using (var first = await StartAsync(root))
            {
                var managed = await first.CreateManagedDatabaseAsync();
                var services = first.Server.Services;
                database = await services.GetRequiredService<ITenantDirectory>()
                    .FindDatabaseAsync(managed.Organization, managed.Project, managed.Name, CancellationToken.None)
                    is TenantContext found ? found : throw new InvalidOperationException("the test database is not registered");
                var system = (TursoDatabaseStore)await services.GetRequiredService<TursoStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None);
                await using var setup = await system.BeginRebuildTransactionAsync(CancellationToken.None);
                await setup.SuspendCaptureAsync(CancellationToken.None);
                _ = await setup.LedgerAsync(command =>
                {
                    using var create = command("CREATE TABLE _test_fault (value TEXT NOT NULL);");
                    _ = create.ExecuteNonQuery();
                    using var insert = command("INSERT INTO _test_fault (value) VALUES ('kept');");
                    return insert.ExecuteNonQuery();
                }, CancellationToken.None);
                _ = await setup.CommitAsync(CancellationToken.None);
            }

            // An otherwise unused SYSTEM page lets startup complete before a real retained read encounters the damage.
            await CorruptTablePageAsync(Path.Combine(root, "_system.db"), "_test_fault");
            await using var restarted = await StartAsync(root);
            var restartedServices = restarted.Server.Services;
            var engine = restartedServices.GetRequiredService<IStorageEngine>();
            var copy = await engine.OpenDatabaseAsync(database, CancellationToken.None)
                ?? throw new InvalidOperationException("the test database is missing");
            var systemCopy = (TursoDatabaseStore)await restartedServices.GetRequiredService<TursoStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None);
            await using var read = (TursoStorageTransaction)await systemCopy.BeginTransactionAsync(write: false, CancellationToken.None);
            var error = await Assert.ThrowsAsync<SqliteException>(async () =>
            {
                _ = await read.LedgerAsync(command =>
                {
                    using var query = command("SELECT value FROM _test_fault;");
                    return query.ExecuteScalar();
                }, CancellationToken.None);
            });
            await Assert.That(error!.SqliteErrorCode).IsEqualTo(11);
            var faults = restartedServices.GetRequiredService<IReplicaCopyFaults>();
            await Assert.That(await faults.RefusedAsync(ChangeGroups.System, Ulid.Empty, CancellationToken.None)).IsTrue()
                .Because("the damaged SYSTEM read has not been disposed");
            await Assert.That(await faults.RefusedAsync(database, copy.Incarnation, CancellationToken.None)).IsTrue();
            _ = await Assert.ThrowsAsync<ReplicaCopyFaultedException>(async () =>
            {
                await using var refused = await copy.BeginDurableTransactionAsync(CancellationToken.None);
            });

            using var channel = PeerChannel.Create(restarted.Server.BaseAddress, PeerTls.Credential);
            var peer = new Features.Replication.V1.Replication.ReplicationClient(channel);
            _ = await Assert.ThrowsAsync<Grpc.Core.RpcException>(async () =>
                await peer.DigestAsync(new DigestRequest { Database = ChangeGroups.Wire(database, null) },
                    deadline: DateTime.UtcNow.AddSeconds(60)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task A_corrupt_tenant_page_read_records_a_copy_fault_before_the_read_transaction_is_released()
    {
        var root = Directory.CreateTempSubdirectory("terrabase-page-fault-").FullName;
        try
        {
            TenantContext database;
            string path;
            await using (var first = await StartAsync(root))
            {
                var managed = await first.CreateManagedDatabaseAsync();
                var services = first.Server.Services;
                database = await services.GetRequiredService<ITenantDirectory>()
                    .FindDatabaseAsync(managed.Organization, managed.Project, managed.Name, CancellationToken.None)
                    is TenantContext found ? found : throw new InvalidOperationException("the test database is not registered");
                var store = await services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(database, CancellationToken.None)
                    ?? throw new InvalidOperationException("the test database is missing");
                await using var write = await store.BeginDurableTransactionAsync(CancellationToken.None);
                _ = await write.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
                _ = await write.PutAsync("items", "kept", "{\"n\":1}"u8.ToArray(), CancellationToken.None);
                _ = await write.CommitAsync(CancellationToken.None);
                var side = services.GetRequiredService<IRebuildStorage>().SidePath(database, Ulid.NewUlid());
                path = Path.Combine(Path.GetDirectoryName(side)!, database.DatabaseId + ".db");
            }

            await CorruptTablePageAsync(path, TursoStorageTransaction.Table("items").Trim('"'));

            await using var restarted = await StartAsync(root);
            var engine = restarted.Server.Services.GetRequiredService<IStorageEngine>();
            var copy = await engine.OpenDatabaseAsync(database, CancellationToken.None)
                ?? throw new InvalidOperationException("the damaged test database is missing");
            await using var held = await copy.BeginDurableTransactionAsync(CancellationToken.None);
            await using var read = await copy.BeginTransactionAsync(write: false, CancellationToken.None);
            var error = await Assert.ThrowsAsync<SqliteException>(async () =>
            {
                _ = await read.GetAsync("items", "kept", CancellationToken.None);
            });
            await Assert.That(error!.SqliteErrorCode).IsEqualTo(11);
            var faults = restarted.Server.Services.GetRequiredService<IReplicaCopyFaults>();
            await Assert.That(await faults.RefusedAsync(database, copy.Incarnation, CancellationToken.None)).IsTrue()
                .Because("the read is still active, before the durable fault record");
            await Assert.That(await restarted.Server.Services.GetRequiredService<IRebuildLedger>()
                .GetAsync(database, CancellationToken.None)).IsNull();
            _ = await Assert.ThrowsAsync<ReplicaCopyFaultedException>(async () => await held.CommitAsync(CancellationToken.None));
            _ = await Assert.ThrowsAsync<ReplicaCopyFaultedException>(async () =>
            {
                await using var refused = await copy.BeginDurableTransactionAsync(CancellationToken.None);
            });

            await read.DisposeAsync();
            var row = await restarted.Server.Services.GetRequiredService<IRebuildLedger>().GetAsync(database, CancellationToken.None);
            await Assert.That(row?.Phase).IsEqualTo(RebuildPhase.Pending);
            _ = await Assert.ThrowsAsync<ReplicaCopyFaultedException>(async () =>
            {
                await using var refused = await copy.BeginDurableTransactionAsync(CancellationToken.None);
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Side_copy_keys_are_bounded_before_reading_and_payload_growth_after_measurement_is_refused(bool oversizedKey)
    {
        var root = Directory.CreateTempSubdirectory("terrabase-salvage-bounds-").FullName;
        try
        {
            await using var api = await StartAsync(root);
            var managed = await api.CreateManagedDatabaseAsync();
            var services = api.Server.Services;
            var database = await services.GetRequiredService<ITenantDirectory>()
                .FindDatabaseAsync(managed.Organization, managed.Project, managed.Name, CancellationToken.None)
                is TenantContext found ? found : throw new InvalidOperationException("the test database is not registered");
            var store = await services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(database, CancellationToken.None)
                ?? throw new InvalidOperationException("the test database is missing");
            await using (var write = await store.BeginDurableTransactionAsync(CancellationToken.None))
            {
                _ = await write.CreateCollectionAsync(new CollectionInfo("items", 1), CancellationToken.None);
                _ = await write.PutAsync("items", "kept", "{\"n\":1}"u8.ToArray(), CancellationToken.None);
                _ = await write.CommitAsync(CancellationToken.None);
            }

            var storage = services.GetRequiredService<IRebuildStorage>();
            var old = await storage.IncarnationAsync(database, CancellationToken.None)
                ?? throw new InvalidOperationException("the test incarnation is missing");
            var side = storage.SidePath(database, old);
            await services.GetRequiredService<IRebuildLedger>().SaveAsync(
                new RebuildRecord(database, old, side, RebuildPhase.Pending, null), CancellationToken.None);
            await Assert.That(await storage.SwapAsync(database, old, side, CancellationToken.None)).IsNotNull();
            var working = await storage.CopyForSalvageAsync(side, CancellationToken.None);
            try
            {
                var measured = await storage.MeasureSideAsync(working, string.Empty, string.Empty, 1024, 2 * 1024 * 1024, CancellationToken.None);
                await Assert.That(measured.Count).IsGreaterThan(0);
                using (var connection = Connection(working))
                {
                    await connection.OpenAsync();
                    using var damage = connection.CreateCommand();
                    damage.CommandText = oversizedKey
                        ? "UPDATE _tb_state SET id = $large WHERE collection = 'items' AND id = 'kept';"
                        : "UPDATE _tb_state SET state = state || $large WHERE collection = 'items' AND id = 'kept';";
                    damage.Parameters.AddWithValue("$large", new string('x', 1024 * 1024));
                    await Assert.That(await damage.ExecuteNonQueryAsync()).IsEqualTo(1);
                }

                _ = await Assert.ThrowsAsync<InvalidDataException>(async () =>
                {
                    if (oversizedKey)
                    {
                        _ = await storage.MeasureSideAsync(working, string.Empty, string.Empty, 1024, 2 * 1024 * 1024, CancellationToken.None);
                    }
                    else
                    {
                        _ = await storage.ReadSideAsync(working, measured, CancellationToken.None);
                    }
                });
                await Assert.That(storage.SideExists(side)).IsTrue();
            }
            finally
            {
                await storage.DiscardWorkingCopyAsync(working, CancellationToken.None);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task A_stale_salvage_step_cannot_overwrite_a_fault_of_the_replacement()
    {
        var root = Directory.CreateTempSubdirectory("terrabase-ledger-race-").FullName;
        try
        {
            await using var api = await StartAsync(root);
            var managed = await api.CreateManagedDatabaseAsync();
            var services = api.Server.Services;
            var database = await services.GetRequiredService<ITenantDirectory>()
                .FindDatabaseAsync(managed.Organization, managed.Project, managed.Name, CancellationToken.None)
                is TenantContext found ? found : throw new InvalidOperationException("the test database is not registered");
            var storage = services.GetRequiredService<IRebuildStorage>();
            var ledger = services.GetRequiredService<IRebuildLedger>();
            var old = await storage.IncarnationAsync(database, CancellationToken.None)
                ?? throw new InvalidOperationException("the test database is missing");
            var pending = new RebuildRecord(database, old, storage.SidePath(database, old), RebuildPhase.Pending, null);
            await ledger.SaveAsync(pending, CancellationToken.None);
            var replacement = await storage.SwapAsync(database, old, pending.SidePath, CancellationToken.None)
                ?? throw new InvalidOperationException("the test copy did not swap");
            var swapped = pending with { Phase = RebuildPhase.Swapped, NewIncarnation = replacement };
            await ledger.SaveAsync(swapped, CancellationToken.None);

            await services.GetRequiredService<IReplicaCopyFaults>()
                .RecordAsync(database, replacement, "an uncertain replacement", CancellationToken.None);
            _ = await Assert.ThrowsAsync<ReplicaCopyFaultedException>(async () =>
                await ledger.SaveAsync(swapped with { Phase = RebuildPhase.Salvaged }, CancellationToken.None));

            var retained = await ledger.GetAsync(database, CancellationToken.None);
            await Assert.That(retained?.Phase).IsEqualTo(RebuildPhase.Pending);
            await Assert.That(retained?.NewIncarnation).IsNull();
            await Assert.That(retained?.AdditionalIncarnation).IsEqualTo(replacement);
            await Assert.That(storage.SideExists(pending.SidePath)).IsTrue();
            await Assert.That(await storage.IncarnationAsync(database, CancellationToken.None)).IsEqualTo(replacement);
            await Assert.That(await storage.FinalizeAsync(database, replacement, pending.SidePath, CancellationToken.None)).IsFalse();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task A_restored_certificate_without_its_tenant_barrier_never_deletes_the_side_copy(bool foreignMember, bool mismatchedReceiver)
    {
        var root = Directory.CreateTempSubdirectory("terrabase-ledger-restore-").FullName;
        try
        {
            TenantContext database;
            Ulid rebuilt;
            uint member;
            var old = Ulid.NewUlid();
            string side;
            await using (var first = await StartAsync(root))
            {
                var managed = await first.CreateManagedDatabaseAsync();
                database = await first.Server.Services.GetRequiredService<ITenantDirectory>()
                    .FindDatabaseAsync(managed.Organization, managed.Project, managed.Name, CancellationToken.None)
                    is TenantContext found ? found : throw new InvalidOperationException("the test database is not registered");
                var engine = first.Server.Services.GetRequiredService<TursoStorageEngine>();
                rebuilt = await engine.IncarnationAsync(database, CancellationToken.None)
                    ?? throw new InvalidOperationException("the test database is missing");
                member = engine.ReplicaId.Member;
                side = engine.SidePath(database, old);
            }

            // A SYSTEM snapshot from after certification beside a tenant snapshot from before its barrier. The retained
            // copy is test-owned; changing its incarnation only constructs this offline restore fixture.
            File.Copy(Path.Combine(Path.GetDirectoryName(side)!, database.DatabaseId + ".db"), side);
            File.Copy(Path.Combine(Path.GetDirectoryName(side)!, database.DatabaseId + ".db.key"), side + ".key");
            using (var connection = Connection(side))
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE commit_offset SET incarnation = $old WHERE id = 1;";
                command.Parameters.AddWithValue("$old", old.ToString());
                _ = await command.ExecuteNonQueryAsync();
            }

            using (var connection = Connection(Path.Combine(root, "_system.db")))
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO _tb_rebuild
                        (organization, project, database, member, old_incarnation, phase, new_incarnation, reason, source_machine, source_incarnation, source_head)
                    VALUES ($organization, $project, $database, $member, $old, $phase, $new, 'restored test certificate', 'source', $source, 1);
                    """;
                command.Parameters.AddWithValue("$organization", database.OrganizationId);
                command.Parameters.AddWithValue("$project", database.ProjectId);
                command.Parameters.AddWithValue("$database", database.DatabaseId);
                command.Parameters.AddWithValue("$member", (long)(foreignMember ? member == 1 ? 2U : 1U : member));
                command.Parameters.AddWithValue("$old", old.ToString());
                command.Parameters.AddWithValue("$phase", (long)RebuildPhase.Certified);
                command.Parameters.AddWithValue("$new", (mismatchedReceiver ? Ulid.NewUlid() : rebuilt).ToString());
                command.Parameters.AddWithValue("$source", Ulid.NewUlid().ToString());
                _ = await command.ExecuteNonQueryAsync();
            }

            var preserved = await File.ReadAllBytesAsync(side);
            await using var restored = await StartAsync(root);
            var services = restored.Server.Services;
            var store = await services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(database, CancellationToken.None)
                ?? throw new InvalidOperationException("the restored tenant file is missing");
            var record = await services.GetRequiredService<IRebuildLedger>().GetAsync(database, CancellationToken.None);
            await Assert.That(record?.Phase).IsEqualTo(foreignMember || !mismatchedReceiver ? RebuildPhase.Swapped : RebuildPhase.Certified);
            await Assert.That(await services.GetRequiredService<IReplicaCopyFaults>()
                .RefusedAsync(database, rebuilt, CancellationToken.None)).IsTrue();
            _ = await Assert.ThrowsAsync<ReplicaCopyFaultedException>(async () =>
            {
                await using var refused = await store.BeginDurableTransactionAsync(CancellationToken.None);
            });
            await Assert.That(await services.GetRequiredService<IRebuildRepair>()
                .CompleteRebuildAsync(database, old, rebuilt, CancellationToken.None)).IsFalse();
            await Assert.That(await services.GetRequiredService<IRebuildStorage>()
                .FinalizeAsync(database, rebuilt, side, CancellationToken.None)).IsFalse();
            await Assert.That((await File.ReadAllBytesAsync(side)).SequenceEqual(preserved)).IsTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static Task<ManagementApi> StartAsync(string root) =>
        ManagementApi.StartAsync(settings => settings["data-directory"] = root,
            services => services.AddSingleton<IReplicaRebuild, BusyRebuild>());

    private static async Task CorruptTablePageAsync(string path, string table)
    {
        long offset;
        using (var connection = Connection(path))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            _ = await command.ExecuteNonQueryAsync();
            command.CommandText = "PRAGMA page_size;";
            var pageSize = Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
            command.CommandText = "SELECT rootpage FROM sqlite_schema WHERE name = $table;";
            command.Parameters.AddWithValue("$table", table);
            var page = Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
            await Assert.That(page).IsGreaterThan(1);
            offset = (page - 1) * pageSize;
        }

        // Complement the page's first byte: a fixed 0xFF left an encrypted page unchanged whenever its ciphertext held 0xFF.
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        file.Position = offset;
        var original = file.ReadByte();
        file.Position = offset;
        file.WriteByte((byte)~original);
    }

    private static SqliteConnection Connection(string path) => new(TestMasterKey.ConnectionString(new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = SqliteOpenMode.ReadWrite,
        Pooling = false,
    }));

    private sealed class BusyRebuild : IReplicaRebuild
    {
        public ValueTask<RebuildOutcome> RebuildAsync(TenantContext database, Ulid faultedIncarnation, CancellationToken cancellationToken) =>
            ValueTask.FromResult<RebuildOutcome>(new RebuildOutcome.Busy());
    }
}
