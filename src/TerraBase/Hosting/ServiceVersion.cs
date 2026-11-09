using System.Reflection;

namespace TerraBase.Hosting;

public static class ServiceVersion
{
    public static string Value { get; } = Informational.Split('+')[0];

    // TERRABASE_REVISION in CI images, the checkout's HEAD in local builds.
    public static string? Revision { get; } = Informational.Split('+') is [_, var revision, ..] && revision.Length > 0 ? revision : null;

    internal static string Informational =>
        typeof(ServiceVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
}
