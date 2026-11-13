using System.Diagnostics.Metrics;
using TerraBase.Configuration;
using TerraBase.Hosting;

namespace TerraBase.Features.Regions;

internal sealed class SqlConnectionMetrics(IMeterFactory meters, TerraBaseOptions options)
{
    private readonly Counter<long> _connections = meters.Create(Telemetry.MeterName).CreateCounter<long>("terrabase.sql.connections",
        description: "SQL WebSocket connections accepted by this region.");

    internal void Accepted() => _connections.Add(1, new KeyValuePair<string, object?>("region", options.Region));
}
