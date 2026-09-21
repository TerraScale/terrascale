namespace TerraScale.Auth.Instances;

internal static class InstancesModule
{
    internal static IEnumerable<Type> Endpoints =>
    [
        typeof(CreateInstanceEndpoint),
    ];
}
