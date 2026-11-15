using System.Diagnostics;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Features.Callers;
using TerraBase.Features.Clock;
using TerraBase.Features.Management;
using TerraBase.Features.Management.Logs;
using TerraBase.Features.Management.Usage;
using TerraBase.Features.Postgres;
using TerraBase.Features.Qos;
using TerraBase.Features.Regions;
using TerraBase.Features.Sql.Routing;
using TerraBase.Features.Stats;

namespace TerraBase.Hosting;

internal sealed class CounterPublication(
    IStorageEngine engine,
    QosSessions sessions,
    CallerAdmission callers,
    PostgresRelay relay,
    SqlConnectionMetrics sql,
    WriteRouter routes) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        (engine as TursoStorageEngine ?? throw new UnreachableException()).PublishSeries();
        sessions.PublishSeries();
        callers.PublishSeries();
        relay.PublishSeries();
        sql.PublishSeries();
        routes.PublishSeries();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class CounterPublicationRest(
    RequestLogRecorder logs,
    UsageMeter usage,
    DatabaseStats stats,
    ClosureFaults closures,
    NodeHlcClock clock) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        logs.PublishSeries();
        usage.PublishSeries();
        stats.PublishSeries();
        closures.PublishSeries();
        clock.PublishSeries();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
