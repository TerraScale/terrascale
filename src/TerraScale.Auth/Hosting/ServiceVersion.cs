using System.Reflection;

namespace TerraScale.Auth.Hosting;

/// <summary>The running build's version, reported by the health endpoints so probes can detect deploys.</summary>
public static class ServiceVersion
{
    /// <summary><c>&lt;version&gt;</c>, or <c>&lt;version&gt;+&lt;revision&gt;</c> when built with <c>TS_AUTH_REVISION</c>.</summary>
    public static string Value { get; } =
        typeof(ServiceVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.0.0";
}
