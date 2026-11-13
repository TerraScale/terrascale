using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TerraBase.Engine;
using TerraBase.Tenancy;
using Turso.Data.Sqlite;

public sealed class StartupHook : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>
{
    private static readonly List<IStorageTransaction> Held = [];
    private static readonly List<SqliteConnection> Replay = [];

    public static void Initialize() => DiagnosticListener.AllListeners.Subscribe(new StartupHook());

    public void OnNext(DiagnosticListener listener)
    {
        if (listener.Name == "Microsoft.Extensions.Hosting")
        {
            listener.Subscribe(this);
        }
    }

    public void OnNext(KeyValuePair<string, object?> entry)
    {
        if (entry is { Key: "HostBuilt", Value: IHost host })
        {
            host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStarted.Register(() =>
                _ = ServeAsync(host.Services, host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping));
        }
    }

    public void OnError(Exception error) => throw error;

    public void OnCompleted() => Held.Clear();

    private static async Task ServeAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        try
        {
            using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            var path = Environment.GetEnvironmentVariable("ENGINE_CRASH_SOCKET")!;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(1);
            Console.WriteLine("engine crash hook ready");
            while (!cancellationToken.IsCancellationRequested)
            {
                using var socket = await listener.AcceptAsync(cancellationToken);
                using var stream = new NetworkStream(socket);
                using var reader = new StreamReader(stream);
                await using var writer = new StreamWriter(stream) { AutoFlush = true };
                var parts = (await reader.ReadLineAsync(cancellationToken))!.Split(' ');
                try
                {
                    var engine = services.GetRequiredService<IStorageEngine>();
                    var tenant = new TenantContext(parts[1], PlanTier.Hobby, parts[2], parts[3]);
                    if (parts[0] is "populate" or "backlog")
                    {
                        _ = await engine.CreateDatabaseAsync(tenant, cancellationToken);
                        var store = (await engine.OpenDatabaseAsync(tenant, cancellationToken))!;
                        await using (var initialized = await store.BeginDurableTransactionAsync(cancellationToken))
                        {
                            _ = await initialized.CommitAsync(cancellationToken);
                        }
                        var io = engine.GetType().GetProperty("Io", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine)!;
                        var connectionString = await (Task<string>)store.GetType()
                            .GetMethod("ConnectionStringAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                            .Invoke(store, [io, cancellationToken])!;
                        using var connection = new SqliteConnection(connectionString);
                        connection.Open();
                        using var command = connection.CreateCommand();
                        command.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = FULL;";
                        command.ExecuteNonQuery();
                        if (parts[0] == "backlog")
                        {
                            command.CommandText = """
                                PRAGMA capture_changes = 'off';
                                WITH RECURSIVE rows(n) AS (VALUES(1) UNION ALL SELECT n + 1 FROM rows WHERE n < 60000)
                                INSERT INTO turso_cdc(change_type, table_name, id, before, after)
                                SELECT CASE WHEN n % 10 = 0 THEN 2 ELSE 1 END, 'idle', n, NULL, NULL FROM rows;
                                """;
                            command.ExecuteNonQuery();
                        }
                        else
                        {
                            command.CommandText = "CREATE TABLE payload (id INTEGER PRIMARY KEY, body BLOB);";
                            command.ExecuteNonQuery();
                            var count = int.Parse(parts[4], CultureInfo.InvariantCulture);
                            for (var index = 0; index < count; index++)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                command.CommandText = "INSERT INTO payload(body) VALUES (randomblob(1048576));";
                                command.ExecuteNonQuery();
                            }
                            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                            command.ExecuteNonQuery();
                            command.CommandText = "UPDATE payload SET body = randomblob(1048576) WHERE id <= 16;";
                            command.ExecuteNonQuery();
                        }
                    }
                    else if (parts[0] == "replay")
                    {
                        var store = (await engine.OpenDatabaseAsync(tenant, cancellationToken))!;
                        var io = engine.GetType().GetProperty("Io", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine)!;
                        var connectionString = await (Task<string>)store.GetType()
                            .GetMethod("ConnectionStringAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                            .Invoke(store, [io, cancellationToken])!;
                        var connection = new SqliteConnection(connectionString);
                        Replay.Add(connection);
                        connection.Open();
                        connection.UseManualWal();
                        using var command = connection.CreateCommand();
                        command.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = FULL; UPDATE payload SET body = randomblob(1048576) WHERE id <= 16;";
                        command.ExecuteNonQuery();
                    }
                    else if (parts[0] == "hold")
                    {
                        _ = await engine.CreateDatabaseAsync(tenant, cancellationToken);
                        var store = (await engine.OpenDatabaseAsync(tenant, cancellationToken))!;
                        var held = await store.BeginDurableTransactionAsync(cancellationToken);
                        Held.Add(held);
                    }
                    else if (parts[0] == "check")
                    {
                        var store = (await engine.OpenDatabaseAsync(tenant, cancellationToken))!;
                        await using var read = await store.BeginTransactionAsync(false, cancellationToken);
                        if (await read.GetOffsetAsync(cancellationToken) < 0)
                        {
                            throw new InvalidDataException("the recovered offset is invalid");
                        }
                    }
                    else
                    {
                        var store = (await engine.OpenDatabaseAsync(tenant, cancellationToken))!;
                        var ledger = engine.GetType().GetProperty("Ledger", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine)!;
                        await (Task)ledger.GetType().GetMethod("RecordIntentAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                            .Invoke(ledger, [store, cancellationToken])!;
                        if (parts[0] == "rewrite")
                        {
                            var journal = engine.GetType().GetProperty("IntentStore", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine)!;
                            var io = engine.GetType().GetProperty("Io", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine)!;
                            var connectionString = await (Task<string>)journal.GetType()
                                .GetMethod("ConnectionStringAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                                .Invoke(journal, [io, cancellationToken])!;
                            var incarnation = store.GetType().GetProperty(nameof(IDatabaseStore.Incarnation))!.GetValue(store)!.ToString();
                            store.GetType().GetProperty("RewriteBeforeWriter", BindingFlags.Instance | BindingFlags.NonPublic)!
                                .SetValue(store, (Action)(() =>
                                {
                                    using var connection = new SqliteConnection(connectionString);
                                    connection.Open();
                                    using var command = connection.CreateCommand();
                                    command.CommandText = """
                                        PRAGMA synchronous = FULL;
                                        INSERT INTO _tb_write_intent (organization, project, database, incarnation, run)
                                        VALUES ($organization, $project, $database, $incarnation, 1)
                                        ON CONFLICT (organization, project, database) DO UPDATE SET incarnation = excluded.incarnation, run = excluded.run;
                                        """;
                                    command.Parameters.AddWithValue("$organization", parts[1]);
                                    command.Parameters.AddWithValue("$project", parts[2]);
                                    command.Parameters.AddWithValue("$database", parts[3]);
                                    command.Parameters.AddWithValue("$incarnation", incarnation);
                                    command.ExecuteNonQuery();
                                }));
                            var rewritten = engine.GetType().GetMethod("RewriteAsync")!.Invoke(engine, [tenant, 0L, cancellationToken])!;
                            await (Task)rewritten.GetType().GetMethod("AsTask")!.Invoke(rewritten, null)!;
                        }
                    }
                    await writer.WriteLineAsync("ok".AsMemory(), cancellationToken);
                }
                catch (Exception error)
                {
                    await writer.WriteLineAsync(("error " + error).AsMemory(), cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Console.WriteLine("engine crash hook stopped");
        }
        catch (Exception error)
        {
            Console.WriteLine("engine crash hook failed: " + error);
        }
    }
}
