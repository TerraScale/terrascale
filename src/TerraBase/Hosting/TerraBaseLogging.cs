using TerraScale.Logging;

namespace TerraBase.Hosting;

/// <summary>Logging registration for the TerraBase host.</summary>
public static class TerraBaseLogging
{
    /// <summary>Registers the TerraBase record v1 stdout logs and their filter rules.</summary>
    public static ILoggingBuilder AddTerraBaseLogging(this ILoggingBuilder logging) =>
        logging.AddTerraScaleLogging("terrabase", ("TerraBase.Features.ApiKeys.ApiKeyAuthenticationHandler", LogLevel.Warning),
            // ASP.NET Core logs every challenge at Information: a line per anonymous request (the status probe's 401 check alone).
            ("TerraBase.Features.UserAuth.UserAuthenticationHandler", LogLevel.Warning));
}
