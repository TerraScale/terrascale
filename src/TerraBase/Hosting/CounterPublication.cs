using TerraBase.Engine;
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

internal sealed class CounterPublication(IServiceProvider services) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (services.GetService<IStorageEngine>() is ICounterSource engine)
        {
            engine.PublishSeries();
        }

        Publish<QosSessions>(sessions => sessions.PublishSeries());
        Publish<CallerAdmission>(callers => callers.PublishSeries());
        Publish<PostgresRelay>(relay => relay.PublishSeries());
        Publish<SqlConnectionMetrics>(sql => sql.PublishSeries());
        Publish<WriteRouter>(routes => routes.PublishSeries());
        if (services.GetService<IRequestLogs>() is RequestLogRecorder logs)
        {
            logs.PublishSeries();
        }

        Publish<UsageMeter>(usage => usage.PublishSeries());
        Publish<DatabaseStats>(stats => stats.PublishSeries());
        Publish<ClosureFaults>(closures => closures.PublishSeries());
        Publish<NodeHlcClock>(clock => clock.PublishSeries());
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void Publish<T>(Action<T> publish) where T : class
    {
        if (services.GetService<T>() is { } service)
        {
            publish(service);
        }
    }
}
