using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Engine.Turso;
using TerraBase.Features.Replication;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;
using Turso.Data.Sqlite;
using Turso.Raw.Public;

namespace TerraBase.Fast.Replication;

public sealed class CopySourceFaultTests
{
    private const string CrashRoot = "TERRABASE_TEST_COPY_SOURCE_FAULT_CRASH_ROOT";
    private const string SourceOpening = "copy.source.opening";
    private static readonly ReplicaId Peer = ReplicaId.Of(99, 1, 0);

    [Test]
    public async Task Raw_source_collection_page_corruption_refuses_writes_and_ACKs_without_publishing_a_target()
    {
        using var directory = new TempDirectory();
        Fixture source, unrelated;
        await using (var setup = await StartAsync(directory.Root))
        {
            source = await CreateAsync(setup);
            unrelated = await CreateAsync(setup);
        }
        await CorruptCollectionPageAsync(source.Path);

        Action<string, TenantContext>? checkpoint = null;
        await using (var api = await StartAsync(directory.Root, services =>
            services.AddSingleton<DatabaseLifecycleCheckpoint>((step, database) => checkpoint?.Invoke(step, database))))
        {
            var services = api.Server.Services;
            var engine = services.GetRequiredService<IStorageEngine>();
            var concrete = services.GetRequiredService<TursoStorageEngine>();
            var store = await OpenAsync(api, source.Database);
            var system = (TursoDatabaseStore)await concrete.OpenSystemDatabaseAsync(CancellationToken.None);
            var target = Target(source);
            await Assert.That(store.CurrentWriter()).IsNull();
            await Assert.That(HasIntent(system.FilePath, source, concrete.ReplicaId)).IsFalse()
                .Because("this process has neither read nor written the source through a tenant transaction");
            await Assert.That(await services.GetRequiredService<IRebuildLedger>().GetAsync(source.Database, CancellationToken.None)).IsNull();
            var openedWithIntent = false;
            var refusedBeforeRecord = false;
            var recordedAlready = false;
            checkpoint = (step, database) =>
            {
                if (database != source.Database)
                {
                    return;
                }
                if (step == SourceOpening)
                {
                    openedWithIntent = HasIntent(system.FilePath, source, concrete.ReplicaId);
                }
                else if (step == RebuildLedger.Recording)
                {
                    refusedBeforeRecord = store.CopyFault is not null && concrete.Ledger.Refusal(store) is not null;
                    recordedAlready = HasFault(system.FilePath, source.Database);
                }
            };

            var error = await Assert.ThrowsAsync<TursoNativeException>(async () =>
                await engine.CopyDatabaseAsync(source.Database, target, CancellationToken.None));
            checkpoint = null;
            await Assert.That(error!.StatusCode).IsEqualTo(133U).Because("the raw source's native corruption error must escape unchanged");
            await Assert.That(openedWithIntent).IsTrue().Because("a committed SYSTEM intent precedes the raw source open");
            await Assert.That(refusedBeforeRecord).IsTrue().Because("the source is already refused when durable recording begins");
            await Assert.That(recordedAlready).IsFalse();
            await Assert.That(store.CurrentWriter()).IsNull().Because("only the raw copy reader used this source");
            await AssertNoTargetAsync(engine, source, target);
            await AssertRefusedAsync(api, source);
            await WriteAsync(api, unrelated.Database, "unrelated-after-copy-fault");
            await Assert.That(await HasDocumentAsync(api, unrelated.Database, "unrelated-after-copy-fault")).IsTrue();
        }

        await using var restarted = await StartAsync(directory.Root);
        await AssertRefusedAsync(restarted, source);
        await WriteAsync(restarted, unrelated.Database, "unrelated-after-restart");
        await Assert.That(await HasDocumentAsync(restarted, unrelated.Database, "unrelated-after-restart")).IsTrue();
    }

    [Test]
    public async Task A_private_target_publication_failure_does_not_fault_the_healthy_source()
    {
        using var directory = new TempDirectory();
        Fixture source;
        await using (var setup = await StartAsync(directory.Root))
        {
            source = await CreateAsync(setup);
        }
        Action<string, TenantContext>? checkpoint = null;
        await using var api = await StartAsync(directory.Root, services =>
            services.AddSingleton<DatabaseLifecycleCheckpoint>((step, database) => checkpoint?.Invoke(step, database)));
        var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
        var concrete = api.Server.Services.GetRequiredService<TursoStorageEngine>();
        var system = (TursoDatabaseStore)await concrete.OpenSystemDatabaseAsync(CancellationToken.None);
        var target = Target(source);
        var targetPath = TargetPath(source, target);
        var openedWithIntent = false;
        checkpoint = (step, database) =>
        {
            if (step == SourceOpening && database == source.Database)
            {
                openedWithIntent = HasIntent(system.FilePath, source, concrete.ReplicaId);
                // The source copy can complete; only moving the private target onto this directory must fail.
                Directory.CreateDirectory(targetPath);
            }
        };
        _ = await Assert.ThrowsAsync<IOException>(async () => await engine.CopyDatabaseAsync(source.Database, target, CancellationToken.None));
        checkpoint = null;
        await Assert.That(openedWithIntent).IsTrue();
        await AssertNoTargetAsync(engine, source, target);
        var store = await OpenAsync(api, source.Database);
        await Assert.That(store.CopyFault).IsNull();
        await Assert.That(await api.Server.Services.GetRequiredService<IRebuildLedger>().GetAsync(source.Database, CancellationToken.None)).IsNull();
        await Assert.That(await api.Server.Services.GetRequiredService<IReplicaCopyFaults>()
            .RefusedAsync(source.Database, source.Incarnation, CancellationToken.None)).IsFalse();
        await WriteAsync(api, source.Database, "after-target-failure");
        await Assert.That(await HasDocumentAsync(api, source.Database, "after-target-failure")).IsTrue();
        await Assert.That(await api.Server.Services.GetRequiredService<IChangeGroupSource>()
            .AckAsync(source.Database, source.Incarnation, Peer, 1, default, CancellationToken.None)).IsTrue();
    }

    [Test]
    public async Task A_private_target_corruption_error_does_not_fault_the_healthy_source()
    {
        using var directory = new TempDirectory();
        Fixture source;
        await using (var setup = await StartAsync(directory.Root))
        {
            source = await CreateAsync(setup);
        }
        Action<string, TenantContext>? checkpoint = null;
        await using var api = await StartAsync(directory.Root, services =>
            services.AddSingleton<DatabaseLifecycleCheckpoint>((step, database) => checkpoint?.Invoke(step, database)));
        var services = api.Server.Services;
        var engine = services.GetRequiredService<IStorageEngine>();
        var concrete = services.GetRequiredService<TursoStorageEngine>();
        var system = (TursoDatabaseStore)await concrete.OpenSystemDatabaseAsync(CancellationToken.None);
        var target = Target(source);
        var native = new TursoNativeException(133U, "the private target failed initialization with corruption");
        var openedWithIntent = false;
        var targetInitializing = false;
        checkpoint = (step, database) =>
        {
            if (database != source.Database)
            {
                return;
            }
            if (step == SourceOpening)
            {
                openedWithIntent = HasIntent(system.FilePath, source, concrete.ReplicaId);
            }
            else if (step == "copy.target.initializing")
            {
                targetInitializing = true;
                throw native;
            }
        };
        var error = await Assert.ThrowsAsync<TursoNativeException>(async () =>
            await engine.CopyDatabaseAsync(source.Database, target, CancellationToken.None));
        checkpoint = null;
        await Assert.That(targetInitializing).IsTrue();
        await Assert.That(openedWithIntent).IsTrue();
        await Assert.That(ReferenceEquals(error, native)).IsTrue();
        await Assert.That(error!.StatusCode).IsEqualTo(133U);
        await AssertNoTargetAsync(engine, source, target);
        var store = await OpenAsync(api, source.Database);
        await Assert.That(store.CopyFault).IsNull();
        await Assert.That(await services.GetRequiredService<IRebuildLedger>().GetAsync(source.Database, CancellationToken.None)).IsNull();
        await Assert.That(await services.GetRequiredService<IReplicaCopyFaults>()
            .RefusedAsync(source.Database, source.Incarnation, CancellationToken.None)).IsFalse();
        await WriteAsync(api, source.Database, "after-target-corruption");
        await Assert.That(await HasDocumentAsync(api, source.Database, "after-target-corruption")).IsTrue();
        await Assert.That(await services.GetRequiredService<IChangeGroupSource>()
            .AckAsync(source.Database, source.Incarnation, Peer, 1, default, CancellationToken.None)).IsTrue();
    }

    [Test]
    public async Task A_copy_only_source_crash_before_the_fault_record_is_durable_is_refused_at_startup()
    {
        if (Environment.GetEnvironmentVariable(CrashRoot) is { } childRoot)
        {
            await CrashChildAsync(childRoot);
            return;
        }

        using var directory = new TempDirectory();
        var (exit, output) = await RunChildAsync(directory.Root);
        await Assert.That(exit).IsNotEqualTo(0).Because(output);
        await Assert.That(output).Contains("crashed before raw copy fault recording");
        var crashed = JsonSerializer.Deserialize<Crashed>(await File.ReadAllTextAsync(Path.Combine(directory.Root, "copy-crash.json")))!;
        var database = new TenantContext(crashed.Organization, PlanTier.Hobby, crashed.Project, crashed.Database);
        var source = new Fixture(database, crashed.Path, Ulid.Parse(crashed.Incarnation));
        await using var api = await StartAsync(Path.Combine(directory.Root, "data"));
        var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
        var concrete = api.Server.Services.GetRequiredService<TursoStorageEngine>();
        var system = (TursoDatabaseStore)await concrete.OpenSystemDatabaseAsync(CancellationToken.None);
        await Assert.That((int)concrete.ReplicaId.Epoch).IsGreaterThan(crashed.Epoch);
        await AssertRefusedAsync(api, source);
        await Assert.That(ReadSystemText(system.FilePath, database, "SELECT reason FROM _tb_rebuild WHERE organization = $organization AND project = $project AND database = $database;"))
            .Contains("unclean").Because("the source-use intent, not a committed fault record, survives the crash");
        var target = database with { DatabaseId = crashed.Target };
        await Assert.That(File.Exists(TargetPath(source, target))).IsFalse();
        await Assert.That(await engine.OpenDatabaseAsync(target, CancellationToken.None)).IsNull();
    }

    private static async Task CrashChildAsync(string root)
    {
        var data = Path.Combine(root, "data");
        Fixture source;
        await using (var setup = await StartAsync(data))
        {
            source = await CreateAsync(setup);
        }
        await CorruptCollectionPageAsync(source.Path);
        Action<string, TenantContext>? checkpoint = null;
        await using var api = await StartAsync(data, services =>
            services.AddSingleton<DatabaseLifecycleCheckpoint>((step, database) => checkpoint?.Invoke(step, database)));
        var engine = api.Server.Services.GetRequiredService<IStorageEngine>();
        var concrete = api.Server.Services.GetRequiredService<TursoStorageEngine>();
        var store = await OpenAsync(api, source.Database);
        var system = (TursoDatabaseStore)await concrete.OpenSystemDatabaseAsync(CancellationToken.None);
        var target = Target(source);
        await Assert.That(store.CurrentWriter()).IsNull();
        await Assert.That(HasIntent(system.FilePath, source, concrete.ReplicaId)).IsFalse();
        await File.WriteAllTextAsync(Path.Combine(root, "copy-crash.json"), JsonSerializer.Serialize(new Crashed(
            source.Database.OrganizationId, source.Database.ProjectId!, source.Database.DatabaseId!, source.Path,
            source.Incarnation.ToString(), concrete.ReplicaId.Epoch, target.DatabaseId!)));
        var openedWithIntent = false;
        checkpoint = (step, database) =>
        {
            if (database != source.Database)
            {
                return;
            }
            if (step == SourceOpening)
            {
                openedWithIntent = HasIntent(system.FilePath, source, concrete.ReplicaId);
            }
            if (step == RebuildLedger.Recording && openedWithIntent && store.CopyFault is not null &&
                store.CurrentWriter() is null && !HasFault(system.FilePath, source.Database))
            {
                Environment.FailFast("crashed before raw copy fault recording");
            }
        };
        _ = await engine.CopyDatabaseAsync(source.Database, target, CancellationToken.None);
        throw new InvalidOperationException("the child outlived the raw source fault recording checkpoint");
    }

    private static async Task AssertRefusedAsync(ManagementApi api, Fixture source)
    {
        var services = api.Server.Services;
        var row = await services.GetRequiredService<IRebuildLedger>().GetAsync(source.Database, CancellationToken.None);
        await Assert.That(row?.Phase).IsEqualTo(RebuildPhase.Pending);
        await Assert.That(row?.OldIncarnation).IsEqualTo(source.Incarnation);
        await Assert.That(await services.GetRequiredService<IReplicaCopyFaults>()
            .RefusedAsync(source.Database, source.Incarnation, CancellationToken.None)).IsTrue();
        _ = await Assert.ThrowsAsync<ReplicaCopyFaultedException>(async () => await WriteAsync(api, source.Database, "refused"));
        await Assert.That(await HasDocumentAsync(api, source.Database, "refused")).IsFalse();
        await Assert.That(await services.GetRequiredService<IChangeGroupSource>()
            .AckAsync(source.Database, source.Incarnation, Peer, 1, default, CancellationToken.None)).IsFalse();
    }

    private static async Task AssertNoTargetAsync(IStorageEngine engine, Fixture source, TenantContext target)
    {
        await Assert.That(File.Exists(TargetPath(source, target))).IsFalse();
        await Assert.That(await engine.OpenDatabaseAsync(target, CancellationToken.None)).IsNull();
        await Assert.That(Directory.EnumerateFiles(Path.GetDirectoryName(source.Path)!, $".{target.DatabaseId}.*.creating*").Any()).IsFalse();
    }

    private static Task<ManagementApi> StartAsync(string root, Action<IServiceCollection>? configure = null) =>
        ManagementApi.StartAsync(settings => settings["data-directory"] = root, services =>
        {
            services.AddSingleton<IReplicaRebuild, KeepSource>();
            configure?.Invoke(services);
        }, time: new FakeTimeProvider());

    private static async Task<Fixture> CreateAsync(ManagementApi api)
    {
        var managed = await api.CreateManagedDatabaseAsync();
        var database = await api.Server.Services.GetRequiredService<ITenantDirectory>()
            .FindDatabaseAsync(managed.Organization, managed.Project, managed.Name, CancellationToken.None)
            is TenantContext found ? found : throw new InvalidOperationException("the source database is not registered");
        var store = await OpenAsync(api, database);
        await using var write = await store.BeginDurableTransactionAsync(CancellationToken.None);
        _ = await write.CreateCollectionAsync(new CollectionInfo("healthy", 1), CancellationToken.None);
        _ = await write.CreateCollectionAsync(new CollectionInfo("damaged", 1), CancellationToken.None);
        var body = JsonSerializer.SerializeToUtf8Bytes(new { n = new Faker().Random.Int(1, 1000) });
        _ = await write.PutAsync("healthy", "kept", body, CancellationToken.None);
        _ = await write.PutAsync("damaged", "kept", body, CancellationToken.None);
        _ = await write.CommitAsync(CancellationToken.None);
        return new Fixture(database, store.FilePath, store.Incarnation);
    }

    private static async Task<TursoDatabaseStore> OpenAsync(ManagementApi api, TenantContext database) =>
        (TursoDatabaseStore)(await api.Server.Services.GetRequiredService<TursoStorageEngine>().OpenDatabaseAsync(database, CancellationToken.None)
            ?? throw new InvalidOperationException("the source database is missing"));

    private static async Task WriteAsync(ManagementApi api, TenantContext database, string id)
    {
        var store = await OpenAsync(api, database);
        await using var write = await store.BeginDurableTransactionAsync(CancellationToken.None);
        _ = await write.PutAsync("healthy", id, "{\"n\":2}"u8.ToArray(), CancellationToken.None);
        _ = await write.CommitAsync(CancellationToken.None);
    }

    private static async Task<bool> HasDocumentAsync(ManagementApi api, TenantContext database, string id)
    {
        var store = await OpenAsync(api, database);
        await using var read = await store.BeginTransactionAsync(write: false, CancellationToken.None);
        return await read.GetAsync("healthy", id, CancellationToken.None) is not null;
    }

    private static TenantContext Target(Fixture source) => source.Database with { DatabaseId = EntityIds.New().ToString() };
    private static string TargetPath(Fixture source, TenantContext target) => Path.Combine(Path.GetDirectoryName(source.Path)!, target.DatabaseId + ".db");

    // Separate connections observe committed journal state while the ledger's recording checkpoint owns its gate.
    private static bool HasIntent(string systemPath, Fixture source, ReplicaId replica) =>
        ReadSystemText(Path.Combine(Path.GetDirectoryName(systemPath)!, TursoStorageEngine.IntentName + ".db"), source.Database,
            "SELECT incarnation || ':' || replica FROM _tb_write_intent WHERE organization = $organization AND project = $project AND database = $database;")
        == source.Incarnation + ":" + unchecked((long)replica.Value).ToString(CultureInfo.InvariantCulture);

    private static bool HasFault(string systemPath, TenantContext database) =>
        ReadSystemText(systemPath, database,
            "SELECT old_incarnation FROM _tb_rebuild WHERE organization = $organization AND project = $project AND database = $database;") is not null;

    private static string? ReadSystemText(string path, TenantContext database, string sql)
    {
        using var connection = new SqliteConnection(TestMasterKey.ConnectionString(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$organization", database.OrganizationId);
        command.Parameters.AddWithValue("$project", database.ProjectId);
        command.Parameters.AddWithValue("$database", database.DatabaseId);
        return command.ExecuteScalar() as string;
    }

    private static async Task CorruptCollectionPageAsync(string path)
    {
        long offset;
        using (var connection = new SqliteConnection(TestMasterKey.ConnectionString(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false })))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            _ = await command.ExecuteNonQueryAsync();
            command.CommandText = "PRAGMA page_size;";
            var size = Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
            command.CommandText = "SELECT rootpage FROM sqlite_schema WHERE name = $table;";
            command.Parameters.AddWithValue("$table", TursoStorageTransaction.Table("damaged").Trim('"'));
            var page = Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
            await Assert.That(page).IsGreaterThan(1);
            offset = (page - 1) * size;
        }
        // Complement the page's first byte: a fixed 0xFF left an encrypted page unchanged whenever its ciphertext held 0xFF.
        using (var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
        {
            file.Position = offset;
            var original = file.ReadByte();
            file.Position = offset;
            file.WriteByte((byte)~original);
        }
        // Prove this is isolated collection damage, without establishing a tenant transaction intent on the next host.
        using var healthy = new SqliteConnection(TestMasterKey.ConnectionString(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }));
        await healthy.OpenAsync();
        using var readable = healthy.CreateCommand();
        readable.CommandText = $"SELECT count(*) FROM {TursoStorageTransaction.Table("healthy")};";
        await Assert.That(Convert.ToInt64(await readable.ExecuteScalarAsync(), CultureInfo.InvariantCulture)).IsEqualTo(1);
    }

    private static async Task<(int ExitCode, string Output)> RunChildAsync(string root)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("the test executable path is unavailable");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add(Path.GetFullPath(Environment.GetCommandLineArgs()[0]));
        }
        var type = typeof(CopySourceFaultTests);
        foreach (var argument in new[]
        {
            "--treenode-filter", $"/{type.Assembly.GetName().Name}/{type.Namespace}/{type.Name}/{nameof(A_copy_only_source_crash_before_the_fault_record_is_durable_is_refused_at_startup)}",
            "--no-progress", "--disable-logo", "--exit-on-process-exit", Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
            "--results-directory", Directory.CreateDirectory(Path.Combine(root, "results")).FullName,
        })
        {
            start.ArgumentList.Add(argument);
        }
        start.Environment[CrashRoot] = root;
        start.Environment["TMPDIR"] = Directory.CreateDirectory(Path.Combine(root, "tmp")).FullName;
        using var child = Process.Start(start) ?? throw new InvalidOperationException("the copy crash child did not start");
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

    private sealed record Fixture(TenantContext Database, string Path, Ulid Incarnation);
    private sealed record Crashed(string Organization, string Project, string Database, string Path, string Incarnation, int Epoch, string Target);

    private sealed class KeepSource : IReplicaRebuild
    {
        public ValueTask<RebuildOutcome> RebuildAsync(TenantContext database, Ulid faultedIncarnation, CancellationToken cancellationToken) =>
            ValueTask.FromResult<RebuildOutcome>(new RebuildOutcome.Refused("a raw-copy regression keeps the suspect source"));
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("terrabase-copy-source-fault-").FullName;
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
