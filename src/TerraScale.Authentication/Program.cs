using TerraScale.Authentication;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Features.SigningKeys;
using TerraScale.Authentication.Security;

AuthOptions options;
try
{
    options = AuthOptions.FromProcess(args);
}
catch (AuthConfigurationException error)
{
    // Configuration messages name settings, never their values.
    await Console.Error.WriteLineAsync($"ts-auth: {error.Message}");
    return 2;
}

try
{
    if (!Passwords.SelfTest())
    {
        await Console.Error.WriteLineAsync("ts-auth: the Argon2id self-test failed");
        return 3;
    }
}
catch (Exception error) when (error is not OutOfMemoryException)
{
    // The bundled libsodium could not be extracted or loaded (see DOTNET_BUNDLE_EXTRACT_BASE_DIR).
    await Console.Error.WriteLineAsync($"ts-auth: Argon2id is unavailable ({error.GetType().Name})");
    return 3;
}

WebApplication app;
try
{
    app = App.Build(options);
}
catch (AuthConfigurationException error)
{
    await Console.Error.WriteLineAsync($"ts-auth: {error.Message}");
    return 2;
}

if (args is [GitHubActionsBinding.BindCommand or GitHubActionsBinding.UnbindCommand, ..])
{
    // Operator commands: bind (activate) or unbind the GitHub Actions e2e workload, then exit without serving.
    return await GitHubActionsBinding.RunAsync(args[0], app.Services, CancellationToken.None);
}

if (args is [SigningKeyProvisioning.Command, ..])
{
    // Operator command: provisions signing keys with the server's secrets and exits without serving.
    return await SigningKeyProvisioning.RunAsync(app.Services, CancellationToken.None);
}

await app.RunAsync();
return 0;
