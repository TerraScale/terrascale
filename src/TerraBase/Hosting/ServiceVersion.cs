using System.Reflection;

namespace TerraBase.Hosting;

/// <summary>Version of the running build.</summary>
public static class ServiceVersion
{
    /// <summary>The informational version, without build metadata added by the SDK.</summary>
    public static string Value { get; } =
        typeof(ServiceVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";
}
