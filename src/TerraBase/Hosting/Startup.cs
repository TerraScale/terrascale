using TerraBase.Cluster;
using TerraBase.Configuration;
using TerraBase.Continuity;
using TerraBase.Engine;
using TerraBase.Features.Callers;

namespace TerraBase.Hosting;

internal readonly union StartupPreparation(PreparedStartup, ConfigurationError);

internal readonly union ClusterConfiguration(ClusterOptions, ConfigurationError);

internal sealed record PreparedStartup(
    TerraBaseOptions Options, InternalCredential Credential, CallerRegistry Callers, DatabaseKeyring Keyring, ClusterOptions Cluster, ContinuityOptions Continuity);

internal static class Startup
{
    public static StartupPreparation Prepare(IReadOnlyList<string> args) =>
        TerraBaseOptions.FromProcess(args) switch
        {
            TerraBaseOptions options => InternalCredential.FromProcess(options) switch
            {
                InternalCredential credential => CallerRegistry.FromProcess(TimeProvider.System) switch
                {
                    CallerRegistry callers => DatabaseKeyring.Create(options) switch
                    {
                        DatabaseKeyring keyring => Cluster(options, credential) switch
                        {
                            ClusterOptions cluster => new PreparedStartup(options, credential, callers, keyring, cluster, Continuity(options)),
                            ConfigurationError error => error,
                        },
                        ConfigurationError error => error,
                    },
                    ConfigurationError error => error,
                },
                ConfigurationError error => error,
            },
            ConfigurationError error => error,
        };

    private static ClusterConfiguration Cluster(TerraBaseOptions options, InternalCredential credential) =>
        ClusterOptions.Create(
            new ClusterSettings(options.MachineId, options.Region, options.DataDirectory, options.QuicPort, options.InternalPort, options.PeersFile, credential.CorrelationKey),
            TimeProvider.System) switch
        {
            ClusterOptions cluster => cluster,
            ClusterConfigurationError invalid => new ConfigurationError(string.Concat("the cluster mesh is misconfigured: ", invalid.Message)),
        };

    private static ContinuityOptions Continuity(TerraBaseOptions options) => new()
    {
        Archive = options.Backup is { } backup ? new S3ArchiveSettings(backup.Endpoint, backup.Region, backup.Bucket, backup.AccessKeyId, backup.SecretKey) : null,
    };
}
