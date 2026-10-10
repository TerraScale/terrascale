using Microsoft.Extensions.Logging.Console;

namespace TerraScale.Logging;

/// <summary>Options set in code by <see cref="LoggingBuilderExtensions.AddTerraScaleLogging"/>; deployments do not configure them.</summary>
public sealed class TerraScaleFormatterOptions : ConsoleFormatterOptions
{
    /// <summary>Service name used when <c>TS_SERVICE</c> is unset.</summary>
    public string Service { get; set; } = string.Empty;
}
