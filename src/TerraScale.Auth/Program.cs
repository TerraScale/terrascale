using TerraScale.Auth;
using TerraScale.Auth.Configuration;
using TerraScale.Auth.Security;

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

var app = App.Build(options);
await app.RunAsync();
return 0;
