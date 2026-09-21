
using TerraScale.Dashboard;
using TerraScale.Endpoints;
using TerraScale.Datastar.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);
// dotnet run in Production also needs the build manifest's compressed files.
// Published deployments already carry their assets under wwwroot.
builder.WebHost.UseStaticWebAssets();
var security = DashboardSecurityOptions.Load(builder.Configuration, builder.Environment);
builder.Services.AddSingleton(security);
if (!security.DemoMode)
{
    builder.Services.AddHttpClient<LiveIdentityProvider>(client => client.Timeout = TimeSpan.FromSeconds(5));
    builder.Services.AddSingleton<DashboardStateRepository>();
    builder.Services.AddSingleton<LiveAuthSessionService>();
    builder.Services.AddSingleton<TerrabaseDataPlane>();
}
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    // Keep the default Brotli/Gzip providers and MIME types, which exclude SSE.
});
builder.Services.AddSingleton<DashboardStore>();
builder.Services.AddSingleton<TerraBaseBackend>();
builder.Services.AddDatastar();
builder.Services.AddTerraScaleEndpoints(typeof(Program).Assembly, options =>
{
    options.Title = "TerraScale Dashboard";
    options.Version = "v1";
});

var app = builder.Build();

// MapStaticAssets serves SDK-generated fingerprints, ETags and Brotli/gzip
// variants. Public static assets bypass account activation and persistence.
app.MapStaticAssets().ShortCircuit();
AssetUrls.Initialize(app, app.Environment.IsDevelopment());
app.UseResponseCompression();
app.UseMiddleware<PagePerformanceMiddleware>();
app.UseMiddleware<DashboardAuthMiddleware>();

app.MapTerraScaleEndpoints();
app.MapTerraScaleOpenApi();

app.Run();

/// <summary>Exposes the top-level Program class to WebApplicationFactory-driven tests.</summary>
public partial class Program;
