using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Bogus;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Engine.Sqlite;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Tenancy;

/// <summary>One process owns a data directory, and only a directory in the tenancy layout (or an empty one) is accepted.</summary>
public sealed class DataDirectoryTests
{
    private const string LockFileName = ".terrabase.lock";
    private const string InUse = "data-directory is in use by another TerraBase process; stop it, then start again";
    private const string PreTenancyLayout = "data directory uses the pre-tenancy layout; start with an empty data directory (no automatic migration)";

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
            await using (var api = await ManagementApi.StartAsync(settings => settings["data-directory"] = dataDirectory))
            {
                using var tenant = await api.CreateTenantAsync("documents:read", "documents:write");
                key = await api.Server.IssueAsync(tenant.OrganizationId, "documents:read");
                document = tenant.Database("main", $"/collections/people/documents/{Guid.CreateVersion7()}");
                using var database = await tenant.Data.PutAsync(tenant.Database("main"), null);
                database.EnsureSuccessStatusCode();
                using var collection = await tenant.Data.PostAsJsonAsync(tenant.Database("main", "/collections"), new { name = "people" });
                collection.EnsureSuccessStatusCode();
                using var written = await tenant.Data.PutAsJsonAsync(document, new { name = new Faker().Name.FullName() });
                written.EnsureSuccessStatusCode();
                before = await tenant.Data.GetStringAsync(document);
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
            var database = new TenantContext(Guid.NewGuid().ToString(), PlanTier.Hobby, Guid.NewGuid().ToString(), Guid.NewGuid().ToString());
            await engine.CreateDatabaseAsync(database, CancellationToken.None);
            var store = (await engine.OpenDatabaseAsync(database, CancellationToken.None))!;
            await using var transaction = await store.BeginTransactionAsync(write: true, CancellationToken.None);
            await transaction.PutAsync("items", "a", Encoding.UTF8.GetBytes("""{"n":1}"""), CancellationToken.None);

            // The engine closes its stores while the transaction still holds its session. Checked in process, so the
            // check cannot race the shutdown's grace: the directory stays owned for as long as the session is out.
            var shutdown = engine.DisposeAsync().AsTask();
            await Assert.That(TerraBaseTestServer.IsDataDirectoryOwned(dataDirectory)).IsTrue();

            // The transaction still works; returning its session closes the last connection, which releases the directory.
            await transaction.CommitAsync(CancellationToken.None);
            await transaction.DisposeAsync();
            await shutdown.WaitAsync(TimeSpan.FromSeconds(30));
            await Assert.That(TerraBaseTestServer.IsDataDirectoryOwned(dataDirectory)).IsFalse();
            serverDisposed = true;
            await server.DisposeAsync();

            await using var restarted = await TerraBaseTestServer.StartAsync(settings => settings["data-directory"] = dataDirectory);
            var reopened = (await restarted.Services.GetRequiredService<IStorageEngine>().OpenDatabaseAsync(database, CancellationToken.None))!;
            await using var reader = await reopened.BeginTransactionAsync(write: false, CancellationToken.None);
            await Assert.That((await reader.GetAsync("items", "a", CancellationToken.None))?.Version).IsEqualTo(1);
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
        var database = new TenantContext(Guid.NewGuid().ToString(), PlanTier.Hobby, Guid.NewGuid().ToString(), Guid.NewGuid().ToString());
        await engine.CreateDatabaseAsync(database, CancellationToken.None);
        var store = (await engine.OpenDatabaseAsync(database, CancellationToken.None))!;
        await using var transaction = await store.BeginTransactionAsync(write: true, CancellationToken.None);
        await transaction.PutAsync("items", "a", Encoding.UTF8.GetBytes("""{"n":1}"""), CancellationToken.None);

        // No grace: shutdown returns at once, with the transaction's session still out.
        await engine.DisposeAsync();

        // It said so, closed nothing and kept the directory: the transaction still commits.
        await Assert.That(logs.Messages).Contains(message => message.StartsWith("Warning: Shutdown waited 0 s and stopped waiting with 1 database connections still open", StringComparison.Ordinal));
        await Assert.That(TerraBaseTestServer.IsDataDirectoryOwned(server.DataDirectory)).IsTrue();
        await transaction.CommitAsync(CancellationToken.None);
        await transaction.DisposeAsync();
        await server.Services.GetRequiredService<SqliteStorageEngine>().CleanupCompletion;
        // Its session was the last connection: returning it released the directory.
        await Assert.That(TerraBaseTestServer.IsDataDirectoryOwned(server.DataDirectory)).IsFalse();
    }

    [Test]
    public async Task Evicted_idle_connections_are_closed_before_shutdown_releases_the_data_directory()
    {
        await using var server = await TerraBaseTestServer.StartAsync(settings => settings["shutdown-grace-seconds"] = "1");
        var engine = server.Services.GetRequiredService<SqliteStorageEngine>();
        var project = new TenantContext(Guid.NewGuid().ToString(), PlanTier.Hobby, Guid.NewGuid().ToString());
        // More databases than the pool keeps idle connections for: returning the later sessions evicts earlier ones.
        for (var i = 0; i < engine.Pool.MaxIdle + 8; i++)
        {
            var database = project with { DatabaseId = Guid.NewGuid().ToString() };
            await engine.CreateDatabaseAsync(database, CancellationToken.None);
            var store = (await engine.OpenDatabaseAsync(database, CancellationToken.None))!;
            await (await store.BeginTransactionAsync(write: false, CancellationToken.None)).DisposeAsync();
        }

        await Assert.That(engine.Pool.IdleCount).IsEqualTo(engine.Pool.MaxIdle);

        await engine.DisposeAsync();
        await engine.CleanupCompletion;

        // Every eviction was counted until its connection closed, so shutdown completed: nothing is open, no database
        // file of the directory is held, and the directory is free.
        await Assert.That(engine.Pool.OpenCount).IsEqualTo(0);
        await Assert.That(OpenDatabaseFiles(server.DataDirectory)).IsEmpty();
        await Assert.That(TerraBaseTestServer.IsDataDirectoryOwned(server.DataDirectory)).IsFalse();
    }

    [Test]
    public async Task Shutdown_closes_every_idle_connection_even_when_one_fails_to_close()
    {
        await using var server = await TerraBaseTestServer.StartAsync(settings => settings["shutdown-grace-seconds"] = "1");
        var engine = server.Services.GetRequiredService<SqliteStorageEngine>();
        var database = new TenantContext(Guid.NewGuid().ToString(), PlanTier.Hobby, Guid.NewGuid().ToString(), Guid.NewGuid().ToString());
        await engine.CreateDatabaseAsync(database, CancellationToken.None);
        var path = Path.Combine(server.DataDirectory, database.OrganizationId, database.ProjectId!, database.DatabaseId + ".db");
        // Stores the engine does not track, one session each (so no per-database cap applies), closed by the pool's own
        // stop in the order they were pooled: two whose close fails with different exceptions, then a working one.
        SqliteConnection[] connections =
        [
            new FailingConnection(Connect(path), new InvalidOperationException("closing failed")),
            new FailingConnection(Connect(path), new IOException("closing failed")),
            new SqliteConnection(Connect(path)),
        ];
        foreach (var connection in connections)
        {
            var store = new SqliteDatabaseStore("probe", path, engine.Pool, engine.Io, "PRAGMA cache_size = -64; PRAGMA busy_timeout = 30000;");
            var session = await OpenSessionAsync(connection);
            await Assert.That(engine.Pool.Rent(store)).IsNull();
            engine.Pool.Adopt(store, session);
            engine.Pool.Return(store, session);
            await Assert.That(store.IdleCount).IsEqualTo(1);
        }

        await Assert.That(OpenDatabaseFiles(server.DataDirectory)).IsNotEmpty();

        var error = await Assert.That(async () => await engine.DisposeAsync()).Throws<AggregateException>();

        // The failure is reported after the batch: every connection was closed, the failing one's native handle too,
        // the accounting is balanced, and so the directory was released.
        await Assert.That(error!.InnerExceptions.Select(inner => inner.GetType())).IsEquivalentTo([typeof(InvalidOperationException), typeof(IOException)]);
        await Assert.That(engine.Pool.OpenCount).IsEqualTo(0);
        await Assert.That(OpenDatabaseFiles(server.DataDirectory)).IsEmpty();
        await Assert.That(TerraBaseTestServer.IsDataDirectoryOwned(server.DataDirectory)).IsFalse();
    }

    [Test]
    public async Task The_ownership_probe_fails_instead_of_reading_a_missing_directory_as_owned()
    {
        var missing = Path.Combine(Path.GetTempPath(), "terrabase-missing-" + Guid.NewGuid());

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
    [Arguments("flat", PreTenancyLayout)]
    [Arguments("system-only", PreTenancyLayout)]
    [Arguments("stray-database", PreTenancyLayout)]
    [Arguments("system-prefixed", PreTenancyLayout)]
    [Arguments("unknown-layout", "data directory has a layout this build does not support")]
    public async Task A_data_directory_from_before_tenancy_is_refused_without_changing_a_byte(string layout, string message)
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
                default:
                    // A layout record this build does not know, from a newer build.
                    await File.WriteAllBytesAsync(lockFile, [2]);
                    break;
            }

            var before = Snapshot(dataDirectory);
            var recorded = File.Exists(lockFile) ? await File.ReadAllBytesAsync(lockFile) : [];

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

    private static string Connect(string path) => new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString();

    private static async Task<SqliteSession> OpenSessionAsync(SqliteConnection connection)
    {
        await connection.OpenAsync();
        return new SqliteSession(connection);
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
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, typeof(Program).Assembly.GetName().Name + ".dll"));
        // Like the harness, clear every ambient TERRABASE setting through entry-point arguments.
        foreach (var name in TerraBaseOptions.SettingNames)
        {
            var value = name switch
            {
                "admin-port" => "0",
                "data-directory" => dataDirectory,
                "region" => "test-region",
                _ => string.Empty,
            };
            start.ArgumentList.Add($"--{name}={value}");
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
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
