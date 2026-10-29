using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;
using Turso.Data.Sqlite;

namespace TerraBase.Slow.Tenancy;

/// <summary>One process owns a data directory, and only a directory in the tenancy layout (or an empty one) is accepted.</summary>
public sealed class DataDirectoryTests
{
    private const string LockFileName = ".terrabase.lock";
    private const string ReplicaFileName = ".terrabase.replica";
    private const string InUse = "data-directory is in use by another TerraBase process; stop it, then start again";
    private const string PreTenancyLayout = "data directory uses the pre-tenancy layout; start with an empty data directory (no automatic migration)";
    // A format refusal case passes "format:" and the format the refusal names for the directory.
    private const string Unversioned = "format:unversioned";

    [Test]
    public async Task A_held_directory_lock_stops_startup_before_any_store_opens_and_releasing_it_allows_startup()
    {
        var dataDirectory = Directory.CreateTempSubdirectory("terrabase-ownership-").FullName;
        var lockFile = Path.Combine(dataDirectory, LockFileName);
        try
        {
            await using (var ownership = new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                await AssertStartupRefusedAsync(dataDirectory, InUse);

                await Assert.That(File.Exists(Path.Combine(dataDirectory, "_system.db"))).IsFalse();
                await Assert.That(Directory.GetFileSystemEntries(dataDirectory)).IsEquivalentTo([lockFile]);
            }

            await using var server = await TerraBaseTestServer.StartAsync(settings => settings["data-directory"] = dataDirectory);
            using var health = await server.Client.GetAsync(new Uri("/healthz", UriKind.Relative));

            await Assert.That(health.StatusCode).IsEqualTo(HttpStatusCode.OK);
            // Health does not need a store; the real listener proves startup succeeded after release.
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [Test]
    public async Task A_running_server_refuses_a_second_owner_without_changing_data_and_disposal_allows_restart()
    {
        var dataDirectory = Directory.CreateTempSubdirectory("terrabase-owner-restart-").FullName;
        try
        {
            string key;
            Uri document;
            string before;
            await using (var api = await ManagementApi.StartAsync(settings => settings["data-directory"] = dataDirectory, services =>
            {
                var registration = services.Single(service => service.ServiceType == typeof(TursoStorageEngine));
                services.Remove(registration);
                services.AddSingleton(provider =>
                {
                    var engine = (TursoStorageEngine)registration.ImplementationFactory!(provider);
                    // Keep a completed readiness probe cached while comparing the directory's complete contents.
                    engine.ReadinessClock = new FakeTimeProvider();
                    return engine;
                });
            }))
            {
                using var tenant = await api.CreateTenantAsync("documents:read", "documents:write");
                key = await api.Server.IssueAsync(tenant.OrganizationId, "documents:read");
                document = tenant.Database("main", $"/collections/people/documents/by-key?id={EntityIds.New()}");
                using var database = await tenant.Data.PutAsync(tenant.Database("main"), null);
                database.EnsureSuccessStatusCode();
                using var collection = await tenant.Data.PostAsJsonAsync(tenant.Database("main", "/collections"), new { name = "people" });
                collection.EnsureSuccessStatusCode();
                using var written = await tenant.Data.PutAsJsonAsync(document, new { name = new Faker().Name.FullName() });
                written.EnsureSuccessStatusCode();
                before = await tenant.Data.GetStringAsync(document);
                using var ready = await api.Server.Client.GetAsync(new Uri("/readyz", UriKind.Relative));
                ready.EnsureSuccessStatusCode();
                var files = Directory.GetFileSystemEntries(dataDirectory, "*", SearchOption.AllDirectories);

                await AssertStartupRefusedAsync(dataDirectory, InUse);

                await Assert.That(Directory.GetFileSystemEntries(dataDirectory, "*", SearchOption.AllDirectories)).IsEquivalentTo(files);
                await Assert.That(await tenant.Data.GetStringAsync(document)).IsEqualTo(before);
                using var health = await api.Server.Client.GetAsync(new Uri("/healthz", UriKind.Relative));
                await Assert.That(health.StatusCode).IsEqualTo(HttpStatusCode.OK);
            }

            await using var restarted = await ManagementApi.StartAsync(settings => settings["data-directory"] = dataDirectory);
            // The original key exercises persisted system state as well as the tenant's document after restart.
            using var data = restarted.DataClient(key);
            using var read = await data.GetAsync(document);
            using var restartedHealth = await restarted.Server.Client.GetAsync(new Uri("/healthz", UriKind.Relative));

            await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(await read.Content.ReadAsStringAsync()).IsEqualTo(before);
            await Assert.That(restartedHealth.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [Test]
    public async Task Shutdown_keeps_the_data_directory_until_a_rented_session_is_returned()
    {
        var dataDirectory = Directory.CreateTempSubdirectory("terrabase-shutdown-").FullName;
        // Short: the checks below hold whether or not the grace expires first.
        var server = await TerraBaseTestServer.StartAsync(settings =>
        {
            settings["data-directory"] = dataDirectory;
            settings["shutdown-grace-seconds"] = "1";
        });
        var serverDisposed = false;
        try
        {
            var engine = server.Services.GetRequiredService<IStorageEngine>();
            var database = new TenantContext(EntityIds.New().ToString(), PlanTier.Hobby, EntityIds.New().ToString(), EntityIds.New().ToString());
            await engine.CreateDatabaseAsync(database, CancellationToken.None);
            var store = (await engine.OpenDatabaseAsync(database, CancellationToken.None))!;
            await using var transaction = await store.BeginTransactionAsync(write: true, CancellationToken.None);
            var version = await transaction.PutAsync("items", "a", Encoding.UTF8.GetBytes("""{"n":1}"""), CancellationToken.None);

            // The engine closes its stores while the transaction still holds its session. Checked in process, so the
            // check cannot race the shutdown's grace: the directory stays owned for as long as the session is out.
            var shutdown = engine.DisposeAsync().AsTask();
            await Assert.That(TerraBaseTestServer.IsDataDirectoryOwned(dataDirectory)).IsTrue();

            // The transaction still works; returning its session closes the last connection, which releases the directory.
            await transaction.CommitAsync(CancellationToken.None);
            await transaction.DisposeAsync();
            await shutdown.WaitAsync(TimeSpan.FromSeconds(30));
            await server.Services.GetRequiredService<TursoStorageEngine>().CleanupCompletion
                .WaitAsync(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
            await Assert.That(TerraBaseTestServer.IsDataDirectoryOwned(dataDirectory)).IsFalse();
            serverDisposed = true;
            await server.DisposeAsync();

            await using var restarted = await TerraBaseTestServer.StartAsync(settings => settings["data-directory"] = dataDirectory);
            var reopened = (await restarted.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(database, CancellationToken.None))!;
            await using var reader = await reopened.BeginTransactionAsync(write: false, CancellationToken.None);
            await Assert.That((await reader.GetAsync("items", "a", CancellationToken.None))?.Version).IsEqualTo(version);
        }
        finally
        {
            if (!serverDisposed)
            {
                await server.DisposeAsync();
            }

            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [Test]
    public async Task An_expired_grace_logs_and_keeps_the_data_directory_until_the_session_returns()
    {
        var logs = new CapturedLogs();
        await using var server = await TerraBaseTestServer.StartAsync(
            settings => settings["shutdown-grace-seconds"] = "0",
            services => services.AddSingleton<ILoggerProvider>(logs));
        var engine = server.Services.GetRequiredService<IStorageEngine>();
        var database = new TenantContext(EntityIds.New().ToString(), PlanTier.Hobby, EntityIds.New().ToString(), EntityIds.New().ToString());
        await engine.CreateDatabaseAsync(database, CancellationToken.None);
        var store = (await engine.OpenDatabaseAsync(database, CancellationToken.None))!;
        await using var transaction = await store.BeginTransactionAsync(write: true, CancellationToken.None);
        await transaction.PutAsync("items", "a", Encoding.UTF8.GetBytes("""{"n":1}"""), CancellationToken.None);

        // No grace: shutdown returns at once, with the transaction's session still out.
        await engine.DisposeAsync();

        // It said so, closed nothing and kept the directory: the transaction still commits.
        // At least this transaction's writer connection; a background write of the system database may still hold its own.
        await Assert.That(logs.Messages).Contains(message => message.StartsWith("Warning: Shutdown waited 0 s and stopped waiting with ", StringComparison.Ordinal)
            && message.Contains(" database connections still open", StringComparison.Ordinal) && !message.Contains("with 0 database", StringComparison.Ordinal));
        await Assert.That(TerraBaseTestServer.IsDataDirectoryOwned(server.DataDirectory)).IsTrue();
        await transaction.CommitAsync(CancellationToken.None);
        await transaction.DisposeAsync();
        await server.Services.GetRequiredService<TursoStorageEngine>().CleanupCompletion;
        // Its session was the last connection: returning it released the directory.
        await Assert.That(TerraBaseTestServer.IsDataDirectoryOwned(server.DataDirectory)).IsFalse();
    }

    [Test]
    public async Task Evicted_idle_connections_are_closed_before_shutdown_releases_the_data_directory()
    {
        // Keep periodic catalog readers from renting between the controlled returns and the idle-count assertion.
        await using var server = await TerraBaseTestServer.StartAsync(settings => settings["shutdown-grace-seconds"] = "1",
            services => services.AddSingleton<TimeProvider>(new FakeTimeProvider(DateTimeOffset.UtcNow)));
        var engine = server.Services.GetRequiredService<TursoStorageEngine>();
        var database = new TenantContext(EntityIds.New().ToString(), PlanTier.Hobby, EntityIds.New().ToString(), EntityIds.New().ToString());
        await engine.CreateDatabaseAsync(database, CancellationToken.None);
        var path = Path.Combine(server.DataDirectory, database.OrganizationId, database.ProjectId!, database.DatabaseId + ".db");
        List<SqliteConnection> connections = [];
        List<TursoDatabaseStore> stores = [];
        // Untracked stores, one session each: background readers cannot rent these, and the per-database cap does not apply.
        for (var i = 0; i < engine.Pool.MaxIdle + 8; i++)
        {
            var store = new TursoDatabaseStore("probe", path, engine.Pool, engine.Io, "PRAGMA cache_size = -64; PRAGMA busy_timeout = 30000;", engine.Encryption);
            stores.Add(store);
            var connection = new SqliteConnection(Connect(path));
            var session = await OpenSessionAsync(connection);
            connections.Add(connection);
            await Assert.That(engine.Pool.Rent(store)).IsNull();
            engine.Pool.Adopt(store, session);
            engine.Pool.Return(store, session);
        }

        await Assert.That(stores.Take(8).All(store => engine.Pool.IdleCountOf(store) == 0)).IsTrue();
        await Assert.That(engine.Pool.IdleCount).IsEqualTo(engine.Pool.MaxIdle);
        await Assert.That(connections.Count(connection => connection.State == System.Data.ConnectionState.Closed)).IsEqualTo(8);

        // Stop workers before their engine, matching production shutdown.
        var completion = server.Services.GetRequiredService<ApplicationRunCompletion>().Completion.Task;
        server.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
        await completion.WaitAsync(TestContext.Current!.Execution.CancellationToken);
        await engine.CleanupCompletion;

        // Every eviction was counted until its connection closed, so shutdown completed: nothing is open, no database
        // file of the directory is held, and the directory is free.
        await Assert.That(engine.Pool.OpenCount).IsEqualTo(0);
        await Assert.That(connections.All(connection => connection.State == System.Data.ConnectionState.Closed)).IsTrue();
        await Assert.That(OpenDatabaseFiles(server.DataDirectory)).IsEmpty();
        await Assert.That(TerraBaseTestServer.IsDataDirectoryOwned(server.DataDirectory)).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Shutdown_closes_every_idle_connection_even_when_one_fails_to_close(bool disposeEngineFirst)
    {
        var server = await TerraBaseTestServer.StartAsync(settings => settings["shutdown-grace-seconds"] = "1");
        Exception? failure = null;
        Exception? cleanupFailure = null;
        try
        {
            var engine = server.Services.GetRequiredService<TursoStorageEngine>();
            var database = new TenantContext(EntityIds.New().ToString(), PlanTier.Hobby, EntityIds.New().ToString(), EntityIds.New().ToString());
            await engine.CreateDatabaseAsync(database, CancellationToken.None);
            var path = Path.Combine(server.DataDirectory, database.OrganizationId, database.ProjectId!, database.DatabaseId + ".db");
            // Stores the engine does not track, one session each (so no per-database cap applies), closed by the pool's own
            // stop in the order they were pooled: two whose close fails with different exceptions, then a working one.
            Exception[] failures = [new InvalidOperationException("closing failed"), new IOException("closing failed")];
            SqliteConnection[] connections =
            [
                new FailingConnection(Connect(path), failures[0]),
                new FailingConnection(Connect(path), failures[1]),
                new SqliteConnection(Connect(path)),
            ];
            foreach (var connection in connections)
            {
                var store = new TursoDatabaseStore("probe", path, engine.Pool, engine.Io, "PRAGMA cache_size = -64; PRAGMA busy_timeout = 30000;", engine.Encryption);
                var session = await OpenSessionAsync(connection);
                await Assert.That(engine.Pool.Rent(store)).IsNull();
                engine.Pool.Adopt(store, session);
                engine.Pool.Return(store, session);
                await Assert.That(store.IdleCount).IsEqualTo(1);
            }

            await Assert.That(OpenDatabaseFiles(server.DataDirectory)).IsNotEmpty();

            var error = await Assert.That(async () =>
            {
                if (disposeEngineFirst)
                {
                    await engine.DisposeAsync();
                    await engine.CleanupCompletion.WaitAsync(TestContext.Current!.Execution.CancellationToken);
                }
                else
                {
                    await server.DisposeAsync();
                }
            }).Throws<AggregateException>();

            // The failure is reported after the batch: every connection was closed, the failing one's native handle too,
            // the accounting is balanced, and so the directory was released.
            await Assert.That(error!.Flatten().InnerExceptions).IsEquivalentTo(failures);
            await Assert.That(engine.Pool.OpenCount).IsEqualTo(0);
            await Assert.That(OpenDatabaseFiles(server.DataDirectory)).IsEmpty();
            await Assert.That(TerraBaseTestServer.IsDataDirectoryOwned(server.DataDirectory)).IsFalse();
            await Assert.That(Directory.Exists(server.DataDirectory)).IsTrue();
        }
        catch (Exception error)
        {
            failure = error;
        }
        finally
        {
            // Harness disposal must observe CleanupCompletion even if engine disposal already reported its fault.
            var resources = ReleaseResourcesAsync();
            try
            {
                await TerraBaseTestServer.WaitForCleanupAsync(resources, TerraBaseTestServer.ReportCleanupFailure,
                    TestContext.Current!.Execution.CancellationToken);
            }
            catch (Exception error)
            {
                cleanupFailure = error;
            }
        }

        if (failure is not null)
        {
            if (cleanupFailure is not null)
            {
                throw new AggregateException(failure, cleanupFailure).Flatten();
            }

            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        await Assert.That(cleanupFailure is AggregateException).IsTrue();
        await Assert.That(((AggregateException)cleanupFailure!).Flatten().InnerExceptions.Select(inner => inner.GetType()))
            .IsEquivalentTo([typeof(InvalidOperationException), typeof(IOException)]);
        await Assert.That(Directory.Exists(server.DataDirectory)).IsFalse();

        async Task ReleaseResourcesAsync()
        {
            try
            {
                await server.CleanupCompletion;
            }
            finally
            {
                server.Client.Dispose();
                if (Directory.Exists(server.DataDirectory))
                {
                    Directory.Delete(server.DataDirectory, recursive: true);
                }
            }
        }
    }

    [Test]
    public async Task The_ownership_probe_fails_instead_of_reading_a_missing_directory_as_owned()
    {
        var missing = Path.Combine(Path.GetTempPath(), "terrabase-missing-" + EntityIds.New().ToString());

        await Assert.That(() => TerraBaseTestServer.IsDataDirectoryOwned(missing)).Throws<DirectoryNotFoundException>();
    }

    [Test]
    public async Task A_broken_lock_file_is_an_error_not_another_owner()
    {
        var dataDirectory = Directory.CreateTempSubdirectory("terrabase-broken-lock-").FullName;
        try
        {
            // Opening a lock file that is a symbolic link to itself fails with ELOOP: an I/O error, but not contention.
            var lockFile = Path.Combine(dataDirectory, LockFileName);
            File.CreateSymbolicLink(lockFile, lockFile);

            await Assert.That(() => TerraBaseTestServer.IsDataDirectoryOwned(dataDirectory)).Throws<IOException>();
            await AssertStartupRefusedAsync(dataDirectory, "data-directory must be a directory where this process can create and lock files");
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [Test]
    public async Task A_started_directory_records_its_storage_format_so_builds_from_before_the_record_refuse_it()
    {
        var dataDirectory = Directory.CreateTempSubdirectory("terrabase-format-").FullName;
        try
        {
            await TerraBaseTestServer.InitializeDataDirectoryAsync(dataDirectory);
            var record = await File.ReadAllBytesAsync(Path.Combine(dataDirectory, LockFileName));

            // The format ID in ASCII. Builds before the version accept only their binary record and stop before opening a
            // file, so a rollback to one cannot migrate this directory.
            await Assert.That(Encoding.ASCII.GetString(record)).IsEqualTo(StorageFormat.Id);
            await Assert.That(StorageFormat.Id).Matches($"^{StorageFormat.Version}-[0-9a-f]{{12}}$");
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [Test]
    [Arguments("flat", PreTenancyLayout)]
    [Arguments("system-only", PreTenancyLayout)]
    [Arguments("stray-database", PreTenancyLayout)]
    [Arguments("system-prefixed", PreTenancyLayout)]
    [Arguments("unrecorded-identity", PreTenancyLayout)]
    [Arguments("unknown-layout", Unversioned)]
    [Arguments("previous-format", Unversioned)]
    [Arguments("digest-format", Unversioned)]
    [Arguments("other-format", "format:1-00000000000f")]
    public async Task A_data_directory_of_another_layout_or_format_is_refused_without_changing_a_byte(string layout, string message)
    {
        // ';' and a quote in the path: every connection string to these files is built, never interpolated.
        var dataDirectory = Directory.CreateTempSubdirectory("terrabase-layout;'-").FullName;
        var lockFile = Path.Combine(dataDirectory, LockFileName);
        try
        {
            switch (layout)
            {
                case "flat":
                    // A database per name next to the system database, one with a journal a writer left behind.
                    await DocumentFiles.WriteAsync(Path.Combine(dataDirectory, "_system.db"), ("api_keys", "k", "{}"));
                    await DocumentFiles.WriteAsync(Path.Combine(dataDirectory, "db-0199a2b3c4d5e6f708192a3b4c5d6e7f.db"), ("people", "p1", "{}"));
                    await File.WriteAllBytesAsync(Path.Combine(dataDirectory, "db-0199a2b3c4d5e6f708192a3b4c5d6e7f.db-wal"), [1, 2, 3]);
                    await DocumentFiles.WriteAsync(Path.Combine(dataDirectory, "shop.db"), ("orders", "o1", "{}"));
                    break;
                case "system-only":
                    // A server from before tenancy with API keys and a catalog, but no client database yet.
                    await DocumentFiles.WriteAsync(Path.Combine(dataDirectory, "_system.db"), ("api_keys", "k", "{}"));
                    await File.WriteAllBytesAsync(Path.Combine(dataDirectory, "_system.db-wal"), [4, 5, 6]);
                    break;
                case "system-prefixed":
                    // Only _system.db and its own journal files are exempt, not any name that starts like them.
                    await TerraBaseTestServer.InitializeDataDirectoryAsync(dataDirectory);
                    await DocumentFiles.WriteAsync(Path.Combine(dataDirectory, "_system.db-old.db"), ("orders", "o1", "{}"));
                    break;
                case "stray-database":
                    // A data directory of this build with a database from before tenancy copied into it.
                    await TerraBaseTestServer.InitializeDataDirectoryAsync(dataDirectory);
                    await DocumentFiles.WriteAsync(Path.Combine(dataDirectory, "shop.db"), ("orders", "o1", "{}"));
                    break;
                case "unrecorded-identity":
                    // A replica identity copied into a directory no server recorded: refused, not adopted.
                    await WriteReplicaAsync(dataDirectory, 2, [7, 0, 0, 0], [0, 0], running: false);
                    break;
                case "previous-format":
                    // What production ran before formats were recorded: the layout byte alone, beside a system database.
                    await File.WriteAllBytesAsync(lockFile, [1]);
                    await DocumentFiles.WriteAsync(Path.Combine(dataDirectory, "_system.db"), ("api_keys", "k", "{}"));
                    break;
                case "digest-format":
                    // What production ran before the version: the layout byte and the 32-byte schema digest.
                    await TerraBaseTestServer.InitializeDataDirectoryAsync(dataDirectory);
                    await File.WriteAllBytesAsync(lockFile, [1, .. SHA256.HashData([1])]);
                    break;
                case "other-format":
                    // This layout, written by another storage format: the refusal names both formats.
                    await TerraBaseTestServer.InitializeDataDirectoryAsync(dataDirectory);
                    await File.WriteAllTextAsync(lockFile, "1-00000000000f");
                    break;
                default:
                    // A layout record this build does not know, from a newer build.
                    await File.WriteAllBytesAsync(lockFile, [99]);
                    break;
            }

            var before = Snapshot(dataDirectory);
            var recorded = File.Exists(lockFile) ? await File.ReadAllBytesAsync(lockFile) : [];
            if (message.StartsWith("format:", StringComparison.Ordinal))
            {
                message = $"data directory has storage format {message[7..]}; this build writes {StorageFormat.Id}; start with an empty data directory (no automatic migration)";
            }

            // Twice: the lock file a refused start leaves behind must not make the next start accept the directory.
            await AssertStartupRefusedAsync(dataDirectory, message);
            await AssertStartupRefusedAsync(dataDirectory, message);

            // Nothing was opened, written, renamed or added (not even a journal next to the system database), and no
            // layout was recorded.
            await Assert.That(Snapshot(dataDirectory)).IsEquivalentTo(before);
            await Assert.That(await File.ReadAllBytesAsync(lockFile)).IsEquivalentTo(recorded);
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    private static string Connect(string path) => TestMasterKey.ConnectionString(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false });

    private static async Task<TursoSession> OpenSessionAsync(SqliteConnection connection)
    {
        await connection.OpenAsync();
        return new TursoSession(connection);
    }

    // Database files under the directory this process holds open, from /proc/self/fd.
    private static List<string> OpenDatabaseFiles(string directory) =>
    [
        .. new DirectoryInfo("/proc/self/fd").EnumerateFileSystemInfos()
            .Select(descriptor => descriptor.LinkTarget)
            .OfType<string>()
            .Where(target => target.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.Ordinal) && target.Contains(".db", StringComparison.Ordinal)),
    ];

    // Every file and directory under the data directory except the lock file, with the SHA-256 of each file.
    private static List<string> Snapshot(string dataDirectory) =>
    [
        .. Directory.EnumerateFileSystemEntries(dataDirectory, "*", SearchOption.AllDirectories)
            .Where(entry => !string.Equals(Path.GetFileName(entry), LockFileName, StringComparison.Ordinal))
            .Select(entry => Path.GetRelativePath(dataDirectory, entry) + " "
                + (Directory.Exists(entry) ? "directory" : Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(entry)))))
            .Order(StringComparer.Ordinal),
    ];

    // TS-647 step 4b (docs/design/anna-core.md, Identities and dots): the member's replica id survives a clean stop, and
    // every other start raises its epoch before anything can mint a dot, so a member recovered from an OS crash never
    // mints again a dot its peers may already hold.
    [Test]
    public async Task A_clean_restart_keeps_the_replica_id_and_any_other_start_raises_its_epoch()
    {
        var dataDirectory = Directory.CreateTempSubdirectory("terrabase-replica-").FullName;
        try
        {
            var (member, epoch) = await ReplicaAsync(dataDirectory);
            await Assert.That(member).IsNotEqualTo(0d);
            await Assert.That(epoch).IsEqualTo(0d);
            await Assert.That(await ReplicaAsync(dataDirectory)).IsEqualTo((member, 0d));
            for (var crash = 1; crash <= 2; crash++)
            {
                // A crash leaves the identity as the start wrote it: still running.
                var identity = await File.ReadAllBytesAsync(Path.Combine(dataDirectory, ReplicaFileName));
                await Assert.That(identity[7]).IsEqualTo((byte)1);
                await WriteReplicaAsync(dataDirectory, 2, identity.AsSpan(1, 4).ToArray(), identity.AsSpan(5, 2).ToArray(), running: true);
                await Assert.That(await ReplicaAsync(dataDirectory)).IsEqualTo((member, (double)crash));
            }

            await Assert.That(await ReplicaAsync(dataDirectory)).IsEqualTo((member, 2d));
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [Test]
    public async Task A_member_past_its_last_epoch_takes_a_new_number()
    {
        var dataDirectory = Directory.CreateTempSubdirectory("terrabase-replica-").FullName;
        try
        {
            var (member, _) = await ReplicaAsync(dataDirectory);
            // The member at epoch 65535, still running: an unclean start past the last epoch.
            var identity = await File.ReadAllBytesAsync(Path.Combine(dataDirectory, ReplicaFileName));
            await WriteReplicaAsync(dataDirectory, 2, identity.AsSpan(1, 4).ToArray(), [0xff, 0xff], running: true);
            var (renumbered, epoch) = await ReplicaAsync(dataDirectory);
            await Assert.That(renumbered).IsNotEqualTo(member);
            await Assert.That(renumbered).IsNotEqualTo(0d);
            await Assert.That(epoch).IsEqualTo(0d);
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [Test]
    public async Task An_unclean_start_carries_the_epoch_past_its_low_byte()
    {
        var dataDirectory = Directory.CreateTempSubdirectory("terrabase-replica-").FullName;
        try
        {
            var (member, _) = await ReplicaAsync(dataDirectory);
            var identity = await File.ReadAllBytesAsync(Path.Combine(dataDirectory, ReplicaFileName));
            await WriteReplicaAsync(dataDirectory, 2, identity.AsSpan(1, 4).ToArray(), [0xff, 0x00], running: true);
            await Assert.That(await ReplicaAsync(dataDirectory)).IsEqualTo((member, 256d));
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [Test]
    [Arguments("empty")]
    [Arguments("a partial first write")]
    [Arguments("a torn epoch")]
    public async Task A_replica_identity_a_crash_left_unfinished_is_a_new_member(string damage)
    {
        var dataDirectory = Directory.CreateTempSubdirectory("terrabase-replica-").FullName;
        try
        {
            var (member, _) = await ReplicaAsync(dataDirectory);
            var path = Path.Combine(dataDirectory, ReplicaFileName);
            var identity = await File.ReadAllBytesAsync(path);
            switch (damage)
            {
                case "empty":
                    // Killed between creating the file and writing it: nothing could have minted under it.
                    await File.WriteAllBytesAsync(path, []);
                    break;
                case "a partial first write":
                    // Killed while the new file grew: only the format byte and part of the member reached disk.
                    await File.WriteAllBytesAsync(path, identity[..3]);
                    break;
                default:
                    // A running record at epoch 0x0100 whose rewrite to 0x0101 a power loss tore after the epoch landed:
                    // the epoch reads 0x0101 under the old checksum, so no part of the record can be trusted.
                    await WriteReplicaAsync(dataDirectory, 2, identity.AsSpan(1, 4).ToArray(), [0x00, 0x01], running: true);
                    identity = await File.ReadAllBytesAsync(path);
                    identity[5] = 1;
                    await File.WriteAllBytesAsync(path, identity);
                    break;
            }

            var (renumbered, epoch) = await ReplicaAsync(dataDirectory);
            await Assert.That(renumbered).IsNotEqualTo(0d);
            await Assert.That(renumbered).IsNotEqualTo(member);
            await Assert.That(epoch).IsEqualTo(0d);
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [Test]
    [Arguments("an unknown format")]
    [Arguments("an unknown format with another checksum")]
    [Arguments("an unknown state")]
    [Arguments("member zero")]
    [Arguments("a short record of another format")]
    [Arguments("a longer record")]
    [Arguments("a much larger file")]
    public async Task A_replica_identity_this_build_cannot_read_stops_the_start(string problem)
    {
        var dataDirectory = Directory.CreateTempSubdirectory("terrabase-replica-").FullName;
        try
        {
            await (await TerraBaseTestServer.StartAsync(settings => settings["data-directory"] = dataDirectory)).DisposeAsync();
            var path = Path.Combine(dataDirectory, ReplicaFileName);
            switch (problem)
            {
                case "an unknown format":
                    await WriteReplicaAsync(dataDirectory, 3, [1, 0, 0, 0], [0, 0], running: false);
                    break;
                case "an unknown format with another checksum":
                    // A newer build may checksum differently: still its identity, not a torn record of this format.
                    await File.WriteAllBytesAsync(path, [3, 1, 0, 0, 0, 0, 0, 1, 0xde, 0xad, 0xbe, 0xef]);
                    break;
                case "an unknown state":
                    await WriteReplicaAsync(dataDirectory, 2, [1, 0, 0, 0], [0, 0], running: false, state: 2);
                    break;
                case "member zero":
                    await WriteReplicaAsync(dataDirectory, 2, [0, 0, 0, 0], [0, 0], running: false);
                    break;
                case "a short record of another format":
                    await File.WriteAllBytesAsync(path, [3, 1, 0]);
                    break;
                case "a much larger file":
                    // Replaced by something else entirely: refused after reading one byte past a record.
                    await File.WriteAllBytesAsync(path, [.. await File.ReadAllBytesAsync(path), .. new byte[1 << 20]]);
                    break;
                default:
                    await File.WriteAllBytesAsync(path, [.. await File.ReadAllBytesAsync(path), 0]);
                    break;
            }

            var identity = await File.ReadAllBytesAsync(path);
            await AssertStartupRefusedAsync(dataDirectory, "data directory has a replica identity this build does not support");
            // Nothing rewrote it: the operator can still inspect what an unknown build left.
            await Assert.That((await File.ReadAllBytesAsync(path)).SequenceEqual(identity)).IsTrue().Because(problem);
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [Test]
    public async Task A_replica_identity_that_is_a_link_stops_the_start_without_touching_its_target()
    {
        var dataDirectory = Directory.CreateTempSubdirectory("terrabase-replica-").FullName;
        var elsewhere = Directory.CreateTempSubdirectory("terrabase-replica-target-").FullName;
        try
        {
            await (await TerraBaseTestServer.StartAsync(settings => settings["data-directory"] = dataDirectory)).DisposeAsync();
            var path = Path.Combine(dataDirectory, ReplicaFileName);
            var target = Path.Combine(elsewhere, ReplicaFileName);
            File.Move(path, target);
            var identity = await File.ReadAllBytesAsync(target);
            File.CreateSymbolicLink(path, target);
            await AssertStartupRefusedAsync(dataDirectory, "data directory has a replica identity that is a link; replace it with the file itself");
            await Assert.That((await File.ReadAllBytesAsync(target)).SequenceEqual(identity)).IsTrue();
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
            Directory.Delete(elsewhere, recursive: true);
        }
    }

    // The member number and epoch a server on dataDirectory reports, after which it stops cleanly. While it serves, its
    // identity is already on disk marked running, as a crash would leave it.
    private static async Task<(double Member, double Epoch)> ReplicaAsync(string dataDirectory)
    {
        await using var server = await TerraBaseTestServer.StartAsync(settings => settings["data-directory"] = dataDirectory);
        var identity = await File.ReadAllBytesAsync(Path.Combine(dataDirectory, ReplicaFileName));
        var (member, epoch) = (await server.MetricAsync("terrabase_replica_member"), await server.MetricAsync("terrabase_replica_epoch"));
        await Assert.That(identity.Length).IsEqualTo(44);
        await Assert.That(identity[7]).IsEqualTo((byte)0);
        await Assert.That(identity.AsSpan(40).SequenceEqual(SHA256.HashData(identity.AsSpan(0, 40)).AsSpan(0, 4))).IsTrue();
        await Assert.That((double)BinaryPrimitives.ReadUInt32LittleEndian(identity.AsSpan(1))).IsEqualTo(member);
        await Assert.That((double)BinaryPrimitives.ReadUInt16LittleEndian(identity.AsSpan(5))).IsEqualTo(epoch);
        return (member, epoch);
    }

    // An identity as a process left it, retaining its owner fingerprint under the updated checksum.
    private static async Task WriteReplicaAsync(string dataDirectory, byte format, byte[] member, byte[] epoch, bool running, byte? state = null)
    {
        var path = Path.Combine(dataDirectory, ReplicaFileName);
        var owner = File.Exists(path) ? (await File.ReadAllBytesAsync(path)).AsSpan(8, 32).ToArray() : new byte[32];
        byte[] record = [format, .. member, .. epoch, state ?? (running ? (byte)0 : (byte)1), .. owner];
        await File.WriteAllBytesAsync(path, [.. record, .. SHA256.HashData(record).AsSpan(0, 4)]);
    }

    [Test]
    public async Task An_established_directory_without_its_operation_journal_refuses_startup()
    {
        var directory = Directory.CreateTempSubdirectory("terrabase-missing-intents-").FullName;
        try
        {
            await using (var server = await TerraBaseTestServer.StartAsync(settings => settings["data-directory"] = directory))
            {
                _ = await server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None);
            }
            var journal = Path.Combine(directory, TursoStorageEngine.IntentName + ".db");
            await Assert.That(File.Exists(journal)).IsTrue();
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm", "-journal" })
            {
                File.Delete(journal + suffix);
            }
            var identity = await File.ReadAllBytesAsync(Path.Combine(directory, ReplicaIdentityFile.FileName));
            await AssertStartupRefusedAsync(directory, "the data directory is missing its storage operation intent journal");
            await Assert.That(File.Exists(journal)).IsFalse();
            await Assert.That(await File.ReadAllBytesAsync(Path.Combine(directory, ReplicaIdentityFile.FileName))).IsEquivalentTo(identity);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // Runs the real binary on the directory: it must print the message and exit with code 2.
    private static async Task AssertStartupRefusedAsync(string dataDirectory, string message)
    {
        // The child inherits the suite's CPU priority: nice locally, full priority in CI.
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // Development, like the harness: outside it a node without a backup bucket refuses to start before any directory check.
        start.Environment["DOTNET_ENVIRONMENT"] = "Development";
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, typeof(Program).Assembly.GetName().Name + ".dll"));
        // Like the harness, clear every ambient TERRABASE setting through entry-point arguments.
        foreach (var name in TerraBaseOptions.SettingNames)
        {
            if (name == "master-key" || BackupSettings.Keys.Contains(name, StringComparer.Ordinal))
            {
                continue;
            }

            var value = name switch
            {
                "port" or "internal-port" or "metrics-port" or "postgres-port" => "0",
                "data-directory" => dataDirectory,
                "region" => "test-region",
                _ => string.Empty,
            };
            start.ArgumentList.Add($"--{name}={value}");
        }

        TestMasterKey.ConfigureProcess(start);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TUnit.Core.TestContext.Current!.Execution.CancellationToken);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("the second TerraBase process did not start");
        try
        {
            // Drain both pipes to the end while waiting for the exit, so output of any size cannot block the child.
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await Task.WhenAll(output, error, process.WaitForExitAsync(timeout.Token));

            await Assert.That(await error).IsEqualTo($"terrabase: {message}{Environment.NewLine}");
            await Assert.That(process.ExitCode).IsEqualTo(2);
            await Assert.That(await output).IsEmpty();
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(cleanup.Token);
            }
        }
    }

    // Every log entry of a server, as "Level: message".
    private sealed class CapturedLogs : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(Messages);

        public void Dispose()
        {
        }

        private sealed class Logger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                messages.Enqueue($"{logLevel}: {formatter(state, exception)}");
        }
    }

    // A connection whose provider close fails, with any exception, before releasing its native handle.
    private sealed class FailingConnection(string connectionString, Exception failure) : SqliteConnection(connectionString)
    {
        public override void Close() => throw failure;
    }
}
