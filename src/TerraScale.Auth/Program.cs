using TerraScale.Auth;
using TerraScale.Auth.Configuration;

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

var app = App.Build(options);
await app.RunAsync();
return 0;
