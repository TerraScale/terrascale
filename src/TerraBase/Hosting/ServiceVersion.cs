using System.Reflection;

namespace TerraBase.Hosting;

/// <summary>Version of the running build.</summary>
public static class ServiceVersion
{
    /// <summary>The informational version, without build metadata added by the SDK.</summary>
    public static string Value { get; } = Informational.Split('+')[0];

    /// <summary>
    /// The source commit the build was made from (<c>TERRABASE_REVISION</c> in CI images, the checkout's HEAD in local builds),
    /// or null when the build had none: which exact commit a node runs.
    /// </summary>
    public static string? Revision { get; } = Informational.Split('+') is [_, var revision, ..] && revision.Length > 0 ? revision : null;

    private static string Informational =>
        typeof(ServiceVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
}
