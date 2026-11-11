using System.Reflection;

namespace TerraScale.Authentication.Hosting;

public static class ServiceVersion
{
    public static string Value { get; } =
        typeof(ServiceVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.0.0";

    public static string Revision { get; } = Value.Split('+')[^1];
}
