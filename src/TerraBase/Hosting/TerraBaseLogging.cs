using TerraScale.Logging;

namespace TerraBase.Hosting;

public static class TerraBaseLogging
{
    public static ILoggingBuilder AddTerraBaseLogging(this ILoggingBuilder logging) =>
        logging.AddTerraScaleLogging("terrabase", ("TerraBase.Features.ApiKeys.ApiKeyAuthenticationHandler", LogLevel.Warning),
            // ASP.NET Core logs every challenge at Information: a line per anonymous request (the status probe's 401 check alone).
            ("TerraBase.Features.UserAuth.UserAuthenticationHandler", LogLevel.Warning));
}
