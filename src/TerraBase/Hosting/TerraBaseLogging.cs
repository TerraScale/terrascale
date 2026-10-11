using TerraScale.Logging;

namespace TerraBase.Hosting;

/// <summary>Logging registration for the TerraBase host.</summary>
public static class TerraBaseLogging
{
    /// <summary>Registers the TerraBase record v1 stdout logs and their filter rules.</summary>
    public static ILoggingBuilder AddTerraBaseLogging(this ILoggingBuilder logging) =>
        logging.AddTerraScaleLogging("terrabase", ("TerraBase.Features.ApiKeys.ApiKeyAuthenticationHandler", LogLevel.Warning));
}
