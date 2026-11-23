using System.Collections.Frozen;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using TerraBase.Continuity;
using TerraBase.Storage;
using TerraScale.Contracts.Cluster;

internal sealed class StartupHook : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>
{
    private const int CachedOpens = 1000;

    private static readonly IReadOnlyDictionary<string, SqlValue> Named = FrozenDictionary<string, SqlValue>.Empty;

    public static void Initialize()
    {
        if (Environment.GetEnvironmentVariable("ENGINE_INTEGRITY_PROXY") is { Length: > 0 } proxy)
        {
            Interpose(proxy);
        }

        _ = DiagnosticListener.AllListeners.Subscribe(new StartupHook());
    }

    public void OnNext(DiagnosticListener value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Name == "Microsoft.Extensions.Hosting")
        {
            _ = value.Subscribe(this);
        }
    }

    public void OnNext(KeyValuePair<string, object?> value)
    {
        if (value is { Key: "HostBuilt", Value: IHost host })
        {
            var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
            _ = lifetime.ApplicationStarted.Register(() => _ = ServeAsync(host.Services, lifetime.ApplicationStopping));
        }
    }

    public void OnError(Exception error) => throw new InvalidOperationException("the hosting diagnostics failed", error);

    public void OnCompleted()
    {
    }

    private static void Interpose(string proxy)
    {
        var storage = typeof(SqliteStorageEngine).Assembly;
        if (storage.GetType("TerraBase.Storage.SqliteLibrary")?.GetField("_registered", BindingFlags.Static | BindingFlags.NonPublic) is not { } registered)
        {
            throw new InvalidOperationException("SqliteLibrary._registered moved; the integrity proxy cannot replace the native resolver");
        }

        registered.SetValue(null, true);
        NativeLibrary.SetDllImportResolver(storage, (name, _, _) => name == "sqlite3mc" ? NativeLibrary.Load(proxy) : 0);
    }

    private static async Task ServeAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        if (Environment.GetEnvironmentVariable("ENGINE_CRASH_SOCKET") is not { Length: > 0 } path)
        {
            Console.WriteLine("engine crash hook failed: ENGINE_CRASH_SOCKET is not set");
            return;
        }

        File.Delete(path);
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen(8);
        Console.WriteLine("engine crash hook ready");
        try
        {
            while (true)
            {
                using var connection = await listener.AcceptAsync(cancellationToken);
                await AnswerAsync(services, connection, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Console.WriteLine("engine crash hook stopped");
        }
    }

    private static async Task AnswerAsync(IServiceProvider services, Socket connection, CancellationToken cancellationToken)
    {
        await using var stream = new NetworkStream(connection);
        using var reader = new StreamReader(stream);
        await using var writer = new StreamWriter(stream) { AutoFlush = true };
        var line = await reader.ReadLineAsync(cancellationToken);
        string answer;
        try
        {
            answer = line?.Split(' ') is [var operation, var database, var count] && int.TryParse(count, CultureInfo.InvariantCulture, out var amount)
                ? await RunAsync(services, operation, database, amount, cancellationToken)
                : "error malformed request " + line;
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            answer = "error " + error;
        }

        await writer.WriteLineAsync(answer.AsMemory(), cancellationToken);
    }

    private static async ValueTask<string> RunAsync(IServiceProvider services, string operation, string database, int count, CancellationToken cancellationToken) =>
        operation switch
        {
            "catalog" => string.Join(' ', await services.GetRequiredService<IContinuityDatabaseCatalog>().ListActiveDatabasesAsync(cancellationToken)),
            "sessions" => Sessions(services),
            "log" => await WithSessionAsync(services, database, (session, token) => LogAsync(services, session, token), cancellationToken),
            "check" => await CheckAsync(services, database, cancellationToken),
            "latency" => await LatencyAsync(services, database, cancellationToken),
            "populate" => await WithSessionAsync(services, database, (session, token) => PopulateAsync(services, session, count, token), cancellationToken),
            _ => "error unknown operation " + operation,
        };

    private static async ValueTask<string> WithSessionAsync(
        IServiceProvider services, string database, Func<ISqlDatabaseSession, CancellationToken, ValueTask<string>> action, CancellationToken cancellationToken)
    {
        if (!Ulid.TryParse(database, out var id))
        {
            return "error invalid database id " + database;
        }

        return await services.GetRequiredService<ContinuitySessions>().OpenAsync(id, cancellationToken) switch
        {
            ISqlDatabaseSession session => await UseAsync(session, action, cancellationToken),
            NotFound missing => string.Concat("error not found ", missing.Code, " ", missing.Message),
            Unavailable unavailable => string.Concat("error unavailable ", unavailable.Code, " ", unavailable.Message),
        };
    }

    private static async ValueTask<string> UseAsync(ISqlDatabaseSession session, Func<ISqlDatabaseSession, CancellationToken, ValueTask<string>> action, CancellationToken cancellationToken)
    {
        await using (session)
        {
            return await action(session, cancellationToken);
        }
    }

    private static async ValueTask<string> LogAsync(IServiceProvider services, ISqlDatabaseSession session, CancellationToken cancellationToken)
    {
        var origin = services.GetRequiredService<StorageEngineOptions>().Region;
        var watermark = session.CurrentBookmark.Get(origin);
        var next = 1L;
        var first = 0L;
        var last = 0L;
        while (true)
        {
            var read = await session.ReadCommitLogAsync(origin, next, 4096, cancellationToken);
            IReadOnlyList<CommitMetadata> entries = read switch
            {
                CommitLogPage page => page.Entries,
                CommitLogGap or StorageBusy => [],
            };
            var resume = read switch
            {
                CommitLogPage page => page.NextSequence,
                CommitLogGap gap => gap.OldestAvailable,
                StorageBusy => next,
            };
            var failure = read switch
            {
                CommitLogPage or CommitLogGap => null,
                StorageBusy busy => busy.Message,
            };
            if (failure is not null)
            {
                return "error commit log unreadable: " + failure;
            }

            if (entries.Count == 0)
            {
                if (resume <= next)
                {
                    break;
                }

                if (first != 0)
                {
                    return string.Create(CultureInfo.InvariantCulture, $"error commit log gap after sequence {last}");
                }

                next = resume;
                continue;
            }

            foreach (var entry in entries)
            {
                if (first != 0 && entry.Sequence != last + 1)
                {
                    return string.Create(CultureInfo.InvariantCulture, $"error commit log sequence {entry.Sequence} follows {last}");
                }

                first = first == 0 ? entry.Sequence : first;
                last = entry.Sequence;
            }

            next = last + 1;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{watermark} {last} {(first == 0 ? 0 : last - first + 1)}");
    }

    private static async ValueTask<string> CheckAsync(IServiceProvider services, string database, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        return await WithSessionAsync(services, database, async (session, token) =>
        {
            if (await ReadAsync(session, token) is { } failure)
            {
                return failure;
            }

            var opened = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var log = await LogAsync(services, session, token);
            return log.StartsWith("error", StringComparison.Ordinal) ? log : string.Create(CultureInfo.InvariantCulture, $"{opened:F3} {log}");
        }, cancellationToken);
    }

    private static async ValueTask<string> LatencyAsync(IServiceProvider services, string database, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        return await WithSessionAsync(services, database, async (session, token) =>
        {
            if (await ReadAsync(session, token) is { } failure)
            {
                return failure;
            }

            var cold = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var repeated = Stopwatch.GetTimestamp();
            for (var index = 0; index < CachedOpens; index++)
            {
                var again = await WithSessionAsync(services, database, static (_, _) => ValueTask.FromResult("ok"), token);
                if (again != "ok")
                {
                    return again;
                }
            }

            var cached = Stopwatch.GetElapsedTime(repeated).TotalMilliseconds / CachedOpens;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"open latency: cold {cold:F4} ms including catalog key resolution and first read; cached {cached:F4} ms/open; {CachedOpens} repetitions"));
            return "ok";
        }, cancellationToken);
    }

    private static async ValueTask<string?> ReadAsync(ISqlDatabaseSession session, CancellationToken cancellationToken)
    {
        var request = new SqlStatementRequest(session.DatabaseId, [new SqlStatement("SELECT count(*) FROM sqlite_schema", [], Named)], SqlAccess.Read,
            ConsistencyMode.Fast, CausalBookmark.Empty);
        return await session.ExecuteReadAsync(request, cancellationToken) switch
        {
            SqlReadResult => null,
            SqlError error => "error sql " + error.Message,
            StorageBusy busy => "error busy " + busy.Message,
            RequestTimeout timeout => "error timeout " + timeout.Message,
        };
    }

    private static async ValueTask<string> PopulateAsync(IServiceProvider services, ISqlDatabaseSession session, int count, CancellationToken cancellationToken)
    {
        var coordinator = services.GetRequiredService<IClusterCoordinator>();
        var request = new SqlStatementRequest(session.DatabaseId, [new SqlStatement("INSERT INTO payload (body) VALUES (randomblob(1048576));", [], Named)],
            SqlAccess.Write, ConsistencyMode.Fast, CausalBookmark.Empty);
        for (var index = 0; index < count; index++)
        {
            var failure = await coordinator.ExecuteWriteAsync(session, request, cancellationToken) switch
            {
                SqlWriteResult => null,
                SqlError error => "error sql " + error.Message,
                SerializationConflict conflict => "error conflict " + conflict.Reason,
                SchemaMismatch => "error schema mismatch",
                Unavailable unavailable => string.Concat("error unavailable ", unavailable.Code, " ", unavailable.Message),
                RequestTimeout timeout => "error timeout " + timeout.Message,
                StorageBusy busy => "error busy " + busy.Message,
            };
            if (failure is not null)
            {
                return failure;
            }
        }

        return "ok";
    }

    private static string Sessions(IServiceProvider services)
    {
        if (Assembly.GetEntryAssembly()?.GetType("TerraBase.Engine.TenantDatabases") is not { } type
            || services.GetService(type) is not { } databases
            || type.GetMethod("OpenSessions")?.Invoke(databases, null) is not IReadOnlyCollection<ISqlDatabaseSession> sessions)
        {
            return "error TenantDatabases.OpenSessions moved";
        }

        return string.Create(CultureInfo.InvariantCulture, $"{sessions.Count} open sessions, managed heap {GC.GetTotalMemory(true) >> 20} MiB");
    }
}
