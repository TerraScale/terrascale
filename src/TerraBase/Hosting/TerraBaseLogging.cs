using TerraScale.Logging;

namespace TerraBase.Hosting;

public static class TerraBaseLogging
{
    public static ILoggingBuilder AddTerraBaseLogging(this ILoggingBuilder logging) =>
        logging.AddTerraScaleLogging("terrabase", ("TerraBase.Features.ApiKeys.ApiKeyAuthenticationHandler", LogLevel.Warning),
            ("TerraBase.Features.UserAuth.UserAuthenticationHandler", LogLevel.Warning));
}
