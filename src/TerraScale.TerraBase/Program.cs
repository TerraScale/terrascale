using TerraScale.TerraBase;
using TerraScale.TerraBase.Configuration;

TerraBaseOptions options;
try
{
    options = TerraBaseOptions.FromProcess(args);
}
catch (TerraBaseConfigurationException error)
{
    // Configuration messages name settings, never their values.
    await Console.Error.WriteLineAsync($"terrabase: {error.Message}");
    return 2;
}

var app = App.Build(options);
await app.RunAsync();
return 0;
