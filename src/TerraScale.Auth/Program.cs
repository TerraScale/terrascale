using TerraScale.Auth.Configuration;

var options = AuthOptions.Load(args);

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = Array.Empty<string>() });
builder.WebHost.UseUrls(options.Listen);

var app = builder.Build();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.Lifetime.ApplicationStarted.Register(() =>
    app.Services.GetRequiredService<ILoggerFactory>()
        .CreateLogger("startup")
        .LogInformation("ts-auth listening on {Listen}", options.Listen));

await app.RunAsync();
