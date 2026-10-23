using System.Reflection;

namespace TerraScale.Authentication.Hosting;

/// <summary>The running build's version: in full for telemetry, and without its revision in the anonymous health bodies.</summary>
public static class ServiceVersion
{
    /// <summary><c>&lt;version&gt;</c>, or <c>&lt;version&gt;+&lt;revision&gt;</c> when built with <c>TS_AUTH_REVISION</c>.</summary>
    public static string Value { get; } =
        typeof(ServiceVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.0.0";

    /// <summary>
    /// <see cref="Value"/> without the <c>+&lt;revision&gt;</c> build metadata, for anonymous health bodies: an exact commit of
    /// this public repository would match the running build to known bugs. Telemetry and Sentry keep <see cref="Value"/>.
    /// </summary>
    public static string Public { get; } = Value.Split('+')[0];
}
