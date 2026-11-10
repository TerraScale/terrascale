using Microsoft.Extensions.Hosting;

namespace TerraBase.Configuration;

internal static class DevelopmentPeers
{
    public static void Refuse(string environmentName)
    {
        if (environmentName == Environments.Development && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TS_PEERS_FILE")))
        {
            throw new TerraBaseConfigurationException("DOTNET_ENVIRONMENT=Development cannot start when TS_PEERS_FILE is set");
        }
    }
}
