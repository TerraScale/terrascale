using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Store;
using ZLogger;

namespace TerraScale.Authentication.Features.SigningKeys;

/// <summary>
/// The idempotent operator command <c>ts-auth provision-signing-key</c>, also used to diagnose startup provisioning.
/// It runs as a separate process on a machine (<c>fly ssh console -C</c>, so only someone with Fly SSH access to the app),
/// with the same secrets as the server and without starting it: it creates ts-auth's collections (idempotent), then the
/// first key if there is none (<see cref="KeyRing.ProvisionAsync"/>), and logs the outcome as an audit line. Existing
/// keys are never replaced; unusable ones fail. Steps: docs/auth/signing-keys.md.
/// </summary>
public static class SigningKeyProvisioning
{
    /// <summary>The command-line verb.</summary>
    public const string Command = "provision-signing-key";

    /// <summary>Provisions the platform issuer's signing keys. Returns whether it created the first key.</summary>
    public static async Task<bool> ProvisionAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        await services.GetRequiredService<AuthStore>().ProvisionAsync(cancellationToken).ConfigureAwait(false);
        return await services.GetRequiredService<KeyRing>().ProvisionAsync(OidcIssuer.InstanceId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs the command: exit code 0 when the issuer has usable keys afterwards, 1 otherwise.</summary>
    public static async Task<int> RunAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(SigningKeyProvisioning));
        try
        {
            var created = await ProvisionAsync(services, cancellationToken).ConfigureAwait(false);
            var outcome = created ? "created the platform issuer's first signing key" : "the platform issuer already has usable signing keys; nothing changed";
            logger.ZLogWarning($"Audit: operator command {Command} {outcome}");
            await Console.Out.WriteLineAsync($"ts-auth {Command}: {outcome}".AsMemory(), cancellationToken).ConfigureAwait(false);
            return 0;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // The type only: messages of storage and crypto failures stay out of the terminal.
            logger.ZLogError(error, $"Audit: operator command {Command} failed");
            await Console.Error.WriteLineAsync($"ts-auth {Command}: failed ({error.GetType().Name}); nothing was replaced".AsMemory(), CancellationToken.None).ConfigureAwait(false);
            return 1;
        }
    }
}
