using Microsoft.Extensions.DependencyInjection;
using TerraBase.Configuration;

namespace TerraBase.Testing;

internal sealed class TestTopology
{
    private readonly string _region;
    private readonly string? _machineId;

    private TestTopology(Dictionary<string, string> values)
    {
        _region = values.GetValueOrDefault("region", "test-region");
        _machineId = values.GetValueOrDefault("node-name");
    }

    public static TestTopology Take(Dictionary<string, string> values)
    {
        var topology = new TestTopology(values);
        _ = values.Remove("region");
        _ = values.Remove("node-name");
        return topology;
    }

    public void ApplyIdentity(IServiceCollection services)
    {
        var registered = services.Single(service => service.ServiceType == typeof(TerraBaseOptions));
        var options = (TerraBaseOptions)registered.ImplementationInstance!;
        services[services.IndexOf(registered)] = ServiceDescriptor.Singleton(options with
        {
            Region = _region,
            MachineId = _machineId ?? options.MachineId,
        });
    }
}
