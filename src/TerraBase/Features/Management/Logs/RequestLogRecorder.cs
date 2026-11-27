using System.Diagnostics;
using System.Threading.Channels;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Hosting;
using TerraScale.Endpoints;
using ZLogger;

namespace TerraBase.Features.Management.Logs;

internal readonly union RequestLogsOutcome(IReadOnlyList<RequestLogEntry>, BackendUnavailable);

public sealed record RequestLogEntry(string Id, Ulid DatabaseId, DateTimeOffset At, string Method, string Path, int Status, long DurationMs, string Region)
{
    public string RequestId => Id;

    public string Route => Path;
}

internal sealed record LoggedDatabase(Ulid DatabaseId);

internal sealed class RequestLogRecorder(SystemDatabase system, TerraBaseOptions options, TimeProvider time, ILogger<RequestLogRecorder> logger) : BackgroundService
{
    private const int Batch = 256;

    private const string InsertSql =
        "INSERT OR IGNORE INTO request_logs (database_id, id, at, method, path, status, duration_ms, region) VALUES (?, ?, ?, ?, ?, ?, ?, ?)";

    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    private static readonly TimeSpan FlushWindow = TimeSpan.FromMilliseconds(200);

    private static readonly TimeSpan ShutdownDrainTimeout = TimeSpan.FromSeconds(5);

    private readonly Channel<RequestLogEntry> _queue = Channel.CreateBounded<RequestLogEntry>(new BoundedChannelOptions(8192)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = false,
    });

    private readonly SemaphoreSlim _writeGate = new(1, 1);

    private readonly CancellationTokenSource _shutdownDrain = new();

    private readonly List<RequestLogEntry> _batchBuffer = new(Batch);

    private TaskCompletionSource _wake = NewWake();

    private int _queued;

    private DateTimeOffset _pruneAt = DateTimeOffset.MinValue;

    public static void Mark(HttpContext context, Ulid databaseId) => context.Features.Set(new LoggedDatabase(databaseId));

    public void Enqueue(Ulid databaseId, string? id, string method, string path, int status, long durationMs)
    {
        var now = time.GetUtcNow();
        var logId = id is not null && RequestIdentity.IsValid(id) ? id : Ulid.NewUlid(now).ToString();
        if (_queue.Writer.TryWrite(new RequestLogEntry(logId, databaseId, now, Method(method), path, status, Math.Max(0, durationMs), options.Region))
            && Interlocked.Increment(ref _queued) >= Batch)
        {
            SignalWake();
        }
    }

    public void Record(HttpContext context, long started)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Features.Get<LoggedDatabase>() is not { } logged)
        {
            return;
        }

        Enqueue(logged.DatabaseId, context.TraceIdentifier, context.Request.Method, context.Request.Path.Value ?? "/",
            context.Response.StatusCode, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    public bool HasGap(string? after) =>
        after is { Length: 26 } && Ulid.TryParse(after, out var cursor) && cursor.Time < time.GetUtcNow() - Retention;

    public ValueTask FlushAsync(CancellationToken cancellationToken) =>
        FlushCoreAsync(_shutdownDrain.Token, cancellationToken);

    private async ValueTask FlushCoreAsync(CancellationToken drainToken, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        if (!_queue.Reader.TryPeek(out _))
        {
            _writeGate.Release();
            return;
        }

        var drain = DrainAndReleaseAsync(drainToken);
        await drain.WaitAsync(cancellationToken);
    }

    private async Task DrainAndReleaseAsync(CancellationToken cancellationToken)
    {
        try
        {
            await DrainLockedAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask<RequestLogsOutcome> ListAsync(
        Ulid databaseId, string? after, int limit, int? minStatus, int? maxStatus, CancellationToken cancellationToken)
    {
        await FlushAsync(cancellationToken);
        var statement = after is null
            ? SqlBuild.Statement(
                "SELECT id, database_id, at, method, path, status, duration_ms, region FROM request_logs "
                + "WHERE database_id = ?1 AND (?2 IS NULL OR status >= ?2) AND (?3 IS NULL OR status <= ?3) ORDER BY at DESC, id DESC LIMIT ?4",
                SqlBuild.Id(databaseId), minStatus is { } min ? SqlBuild.Integer(min) : SqlValue.Null,
                maxStatus is { } max ? SqlBuild.Integer(max) : SqlValue.Null, SqlBuild.Integer(limit))
            : SqlBuild.Statement(
                "SELECT id, database_id, at, method, path, status, duration_ms, region FROM request_logs "
                + "WHERE database_id = ?1 AND id > ?2 AND (?3 IS NULL OR status >= ?3) AND (?4 IS NULL OR status <= ?4) ORDER BY id LIMIT ?5",
                SqlBuild.Id(databaseId), SqlBuild.Text(after), minStatus is { } minAfter ? SqlBuild.Integer(minAfter) : SqlValue.Null,
                maxStatus is { } maxAfter ? SqlBuild.Integer(maxAfter) : SqlValue.Null, SqlBuild.Integer(limit));
        var outcome = await system.QueryAsync(statement, cancellationToken);
        return outcome switch
        {
            IReadOnlyList<IReadOnlyList<SqlValue>> rows => Entries(rows),
            BackendUnavailable unavailable => unavailable,
        };
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        using var timeout = new CancellationTokenSource(ShutdownDrainTimeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        using var cancelInFlight = linked.Token.Register(static state =>
        {
            if (state is CancellationTokenSource cts)
            {
                cts.Cancel();
            }
        }, _shutdownDrain);
        await FlushCoreAsync(linked.Token, linked.Token);
    }

    public override void Dispose()
    {
        _shutdownDrain.Dispose();
        _writeGate.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _pruneAt = time.GetUtcNow();
        using var timer = time.CreateTimer(
            static state => (state as RequestLogRecorder)?.SignalWake(),
            this,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
        while (await _queue.Reader.WaitToReadAsync(stoppingToken))
        {
            if (Volatile.Read(ref _queued) < Batch)
            {
                var wake = ResetWake();
                if (Volatile.Read(ref _queued) < Batch)
                {
                    timer.Change(FlushWindow, Timeout.InfiniteTimeSpan);
                    try
                    {
                        await wake.Task.WaitAsync(stoppingToken);
                    }
                    finally
                    {
                        timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                    }
                }
            }

            await FlushAsync(stoppingToken);
        }
    }

    private async ValueTask DrainLockedAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            _batchBuffer.Clear();
            while (_batchBuffer.Count < Batch && _queue.Reader.TryRead(out var entry))
            {
                _batchBuffer.Add(entry);
            }

            var count = _batchBuffer.Count;
            if (count == 0)
            {
                Volatile.Write(ref _queued, 0);
                return;
            }

            if (Interlocked.Add(ref _queued, -count) < 0)
            {
                Volatile.Write(ref _queued, 0);
            }

            var now = time.GetUtcNow();
            var prune = now >= _pruneAt;
            var statements = new SqlStatement[prune ? count + 1 : count];
            for (var index = 0; index < count; index++)
            {
                var entry = _batchBuffer[index];
                statements[index] = SqlBuild.Statement(
                    InsertSql,
                    SqlBuild.Id(entry.DatabaseId), SqlBuild.Text(entry.Id), SqlBuild.Time(entry.At), SqlBuild.Text(entry.Method), SqlBuild.Text(entry.Path),
                    SqlBuild.Integer(entry.Status), SqlBuild.Integer(entry.DurationMs), SqlBuild.Text(entry.Region));
            }

            _batchBuffer.Clear();
            if (prune)
            {
                _pruneAt = now.AddHours(1);
                statements[count] = SqlBuild.Statement("DELETE FROM request_logs WHERE at < ?", SqlBuild.Time(now - Retention));
            }

            var outcome = await system.WriteTelemetryAsync(statements, ConsistencyMode.Fast, cancellationToken);
            var failure = outcome switch
            {
                SqlWriteResult => null,
                StateConflict conflict => conflict.Message,
                BackendUnavailable unavailable => unavailable.Message,
            };
            if (failure is not null)
            {
                logger.ZLogWarning($"Request logs were dropped: {failure:@Reason}");
            }

            if (count < Batch)
            {
                return;
            }
        }
    }

    private static TaskCompletionSource NewWake() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private TaskCompletionSource ResetWake()
    {
        var next = NewWake();
        Volatile.Write(ref _wake, next);
        return next;
    }

    private void SignalWake() => Volatile.Read(ref _wake).TrySetResult();

    private static string Method(string method) => method switch
    {
        "GET" or "POST" or "PUT" or "PATCH" or "DELETE" or "HEAD" or "OPTIONS" => method,
        _ => "OTHER",
    };

    private static RequestLogsOutcome Entries(IReadOnlyList<IReadOnlyList<SqlValue>> rows) =>
        new([.. rows.Select(static row => new RequestLogEntry(row.TextAt(0), row.IdAt(1), row.TimeAt(2), row.TextAt(3), row.TextAt(4), (int)row.IntegerAt(5),
            row.IntegerAt(6), row.TextAt(7)))]);
}

internal static class RequestLogExtensions
{
    extension(WebApplication app)
    {
        public WebApplication UseTerraBaseRequestLogs()
        {
            var recorder = app.Services.GetRequiredService<RequestLogRecorder>();
            app.Use(async (context, next) =>
            {
                var started = Stopwatch.GetTimestamp();
                try
                {
                    await next(context);
                }
                finally
                {
                    recorder.Record(context, started);
                }
            });
            return app;
        }
    }
}
