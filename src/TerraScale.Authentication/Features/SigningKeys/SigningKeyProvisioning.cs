using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Storage;
using ZLogger;

namespace TerraScale.Authentication.Features.SigningKeys;

public static class SigningKeyProvisioning
{
    public const string Command = "provision-signing-key";

    public static async Task<bool> ProvisionAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        await services.GetRequiredService<AuthSchema>().MigrateAsync(cancellationToken);
        return await services.GetRequiredService<KeyRing>().ProvisionAsync(OidcIssuer.InstanceId, cancellationToken);
    }

    public static async Task<int> RunAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(SigningKeyProvisioning));
        try
        {
            var created = await ProvisionAsync(services, cancellationToken);
            var outcome = created ? "created the platform issuer's first signing key" : "the platform issuer already has usable signing keys; nothing changed";
            logger.ZLogWarning($"Audit: operator command {Command} {outcome}");
            await Console.Out.WriteLineAsync($"ts-auth {Command}: {outcome}".AsMemory(), cancellationToken);
            return 0;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            logger.ZLogError(error, $"Audit: operator command {Command} failed");
            await Console.Error.WriteLineAsync($"ts-auth {Command}: failed ({error.GetType().Name}); nothing was replaced".AsMemory(), CancellationToken.None);
            return 1;
        }
    }
}
