using System.Diagnostics;

namespace TerraScale.DotNet;

/// <summary>
/// OpenTelemetry integration points exposed by the TerraScale SDK.
/// </summary>
public static class TerraScaleTelemetry
{
    /// <summary>
    /// ActivitySource name emitted by TerraScale SDK HTTP operations.
    /// </summary>
    public const string ActivitySourceName = "TerraScale.DotNet";

    /// <summary>
    /// ActivitySource used by the SDK. Register this source with OpenTelemetry tracing.
    /// </summary>
    public static ActivitySource ActivitySource { get; } = new(ActivitySourceName);
}
