using System.Diagnostics;
using System.Threading.Channels;
using TerraBase.Configuration;
using TerraBase.Engine;
using ZLogger;

namespace TerraBase.Features.Management.Logs;

public sealed record RequestLogEntry(string Id, Ulid DatabaseId, DateTimeOffset At, string Method, string Path, int Status, long DurationMs, string Region);

internal sealed record LoggedDatabase(Ulid DatabaseId);

internal sealed class RequestLogRecorder(SystemDatabase system, TerraBaseOptions options, TimeProvider time, ILogger<RequestLogRecorder> logger) : BackgroundService
{
    private const int Batch = 256;

    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    private readonly Channel<RequestLogEntry> _queue = Channel.CreateBounded<RequestLogEntry>(new BoundedChannelOptions(8192)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
    });

    public static void Mark(HttpContext context, Ulid databaseId) => context.Features.Set(new LoggedDatabase(databaseId));

    public void Record(HttpContext context, long started)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Features.Get<LoggedDatabase>() is not { } logged)
        {
            return;
        }

        var now = time.GetUtcNow();
        _queue.Writer.TryWrite(new RequestLogEntry(Ulid.NewUlid(now).ToString(), logged.DatabaseId, now, context.Request.Method, context.Request.Path.Value ?? "/",
            context.Response.StatusCode, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, options.Region));
    }

    public async ValueTask<Result<IReadOnlyList<RequestLogEntry>, Unavailable>> ListAsync(Ulid databaseId, string? after, int limit, CancellationToken cancellationToken) =>
        await system.QueryAsync(SqlBuild.Statement(
            "SELECT id, database_id, at, method, path, status, duration_ms, region FROM request_logs WHERE database_id = ?1 AND (?2 IS NULL OR id > ?2) ORDER BY id LIMIT ?3",
            SqlBuild.Id(databaseId), SqlBuild.OptionalText(after), SqlBuild.Integer(limit)), cancellationToken) switch
        {
            IReadOnlyList<IReadOnlyList<SqlValue>> rows => Entries(rows),
            Unavailable unavailable => unavailable,
        };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pruneAt = time.GetUtcNow();
        await foreach (var first in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            List<RequestLogEntry> batch = [first];
            while (batch.Count < Batch && _queue.Reader.TryRead(out var next))
            {
                batch.Add(next);
            }

            List<SqlStatement> statements = [.. batch.Select(static entry => SqlBuild.Statement(
                "INSERT INTO request_logs (database_id, id, at, method, path, status, duration_ms, region) VALUES (?, ?, ?, ?, ?, ?, ?, ?)",
                SqlBuild.Id(entry.DatabaseId), SqlBuild.Text(entry.Id), SqlBuild.Time(entry.At), SqlBuild.Text(entry.Method), SqlBuild.Text(entry.Path),
                SqlBuild.Integer(entry.Status), SqlBuild.Integer(entry.DurationMs), SqlBuild.Text(entry.Region)))];
            if (time.GetUtcNow() >= pruneAt)
            {
                pruneAt = time.GetUtcNow().AddHours(1);
                statements.Add(SqlBuild.Statement("DELETE FROM request_logs WHERE at < ?", SqlBuild.Time(time.GetUtcNow() - Retention)));
            }

            if (await system.WriteAsync(statements, ConsistencyMode.Fast, stoppingToken) switch
            {
                SqlWriteResult => null,
                Conflict conflict => conflict.Message,
                Unavailable unavailable => unavailable.Message,
            } is { } failure)
            {
                logger.ZLogWarning($"Request logs were dropped: {failure:@Reason}");
            }
        }
    }

    private static Result<IReadOnlyList<RequestLogEntry>, Unavailable> Entries(IReadOnlyList<IReadOnlyList<SqlValue>> rows) =>
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
