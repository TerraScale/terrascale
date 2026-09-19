var (builder, options) = TerraScale.Auth.App.CreateBuilder(args);
var app = builder.Build();
TerraScale.Auth.App.Configure(app, options);
await app.RunAsync();
