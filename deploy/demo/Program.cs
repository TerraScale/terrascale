// args[0]: the directory holding src/TerraScale.Dashboard (published app with web/static).
var origin = "https://" + (Environment.GetEnvironmentVariable("FLY_APP_NAME") ?? "localhost") + ".fly.dev";
var environment = new DemoEnvironment();
await environment.InitializeAsync();
await using var app = environment.CreateHost(args[0], 8080, origin, PublicDemo.Configure);
Console.WriteLine($"DEMO (synthetic data): {origin}; user@example.com / test-password; code 123456");
await app.RunAsync();
