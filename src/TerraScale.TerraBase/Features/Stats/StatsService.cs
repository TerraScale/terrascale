using System.Text.Json;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Features.CrossRegion;
using TerraBase.Hosting;

namespace TerraBase.Features.Stats;

/// <summary>
/// Closes a statistics window every <see cref="Window"/>, publishes it as the cell's rollup and answers the cell's detail
/// requests. Without a cell in the NATS credentials it publishes and answers nothing.
/// </summary>
internal sealed partial class StatsService(DatabaseStats stats, TimeProvider time, StatsMetrics metrics, IStorageEngine engine,
    TerraBaseOptions options, IRegionBus? bus, ILogger<StatsService> logger) : BackgroundService
{
    internal static readonly TimeSpan Window = TimeSpan.FromSeconds(5);
    // A stalled NATS write must not delay the next window close; the timed-out window counts as failed.
    private static readonly TimeSpan PublishTimeout = TimeSpan.FromSeconds(2);
    // One epoch per process: seq restarts with it, so a consumer tells a restart from a gap.
    private readonly string _epoch = Ulid.NewUlid().ToString();
    private long _seq;
    private bool _noCellLogged;
    private Task? _serving;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Window, time);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            // Leave the clock's callback thread, as the flush loops do, so a test clock advanced far ahead does not close windows inline.
            await Task.Yield();
            await PublishAsync(stats.Close(time.GetUtcNow()), stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task PublishAsync(StatsWindow window, CancellationToken stoppingToken)
    {
        if (bus is null)
        {
            return;
        }

        if (bus.Cell is not { } cell)
        {
            metrics.Rollups.Add(1, new KeyValuePair<string, object?>("result", "no_cell"));
            if (!_noCellLogged)
            {
                _noCellLogged = true;
                LogNoCell(logger);
            }

            return;
        }

        _serving ??= ServeAsync(bus, cell, stoppingToken);
        var rollup = StatsRollup.Build(window, cell, options.Region, options.NodeName, _epoch, ++_seq);
        var result = "published";
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            deadline.CancelAfter(PublishTimeout);
            await bus.PublishAsync(StatsSubjects.Rollup(cell), JsonSerializer.SerializeToUtf8Bytes(rollup, TerraBaseJsonContext.Default.StatsRollupV1),
                deadline.Token).ConfigureAwait(false);
        }
        catch (Exception) when (!stoppingToken.IsCancellationRequested)
        {
            result = "failed";
        }

        metrics.Rollups.Add(1, new KeyValuePair<string, object?>("result", result));
    }

    private async Task ServeAsync(IRegionBus bus, string cell, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await bus.ServeAsync(StatsSubjects.Detail(cell), (request, token) => AnswerAsync(cell, request, token), stoppingToken).ConfigureAwait(false);
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                LogServeFailed(logger, error);
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    // The detail answer reads the last closed window; before the first close it lists nothing.
    private async ValueTask<byte[]> AnswerAsync(string cell, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (StatsRollup.ParseRequest(data) is not { } request)
        {
            return JsonSerializer.SerializeToUtf8Bytes(new StatsErrorV1(1, "bad_request"), TerraBaseJsonContext.Default.StatsErrorV1);
        }

        try
        {
            var window = stats.LastWindow;
            var (databases, next) = request.Database is { } database
                ? await StatsRollup.DatabaseAsync(engine, window, database, cancellationToken).ConfigureAwait(false)
                : StatsRollup.Page(window, request.After);
            var reply = new StatsDetailV1(1, cell, options.Region, options.NodeName, _epoch, window?.At ?? time.GetUtcNow(),
                StatsRollup.Milliseconds(window?.Duration ?? Window), databases, next);
            return JsonSerializer.SerializeToUtf8Bytes(reply, TerraBaseJsonContext.Default.StatsDetailV1);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return JsonSerializer.SerializeToUtf8Bytes(new StatsErrorV1(1, "unavailable"), TerraBaseJsonContext.Default.StatsErrorV1);
        }
    }

    [LoggerMessage(LogLevel.Warning, "No cell in the NATS credentials: live statistics are neither published nor answered")]
    private static partial void LogNoCell(ILogger logger);

    [LoggerMessage(LogLevel.Warning, "Answering live statistics requests failed; subscribing again")]
    private static partial void LogServeFailed(ILogger logger, Exception error);
}
