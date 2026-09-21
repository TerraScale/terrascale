namespace TerraScale.Auth.Observability;

internal static class ObservabilityModule
{
    internal static IEnumerable<Type> Endpoints =>
    [
        typeof(HealthEndpoint),
        typeof(ReadinessEndpoint),
        typeof(MetricsEndpoint),
    ];
}
