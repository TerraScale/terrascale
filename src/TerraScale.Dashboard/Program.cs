
using TerraScale.Dashboard;
using TerraScale.Endpoints;
using TerraScale.Datastar.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);
// The Stripe sandbox reads its test keys from a gitignored .env file during
// development; deployed environments provide the same names as real settings.
DotEnvFile.Load(builder.Configuration, builder.Environment);
// dotnet run in Production also needs the build manifest's compressed files.
// Published deployments already carry their assets under wwwroot.
builder.WebHost.UseStaticWebAssets();
var security = DashboardSecurityOptions.Load(builder.Configuration, builder.Environment);
builder.Services.AddSingleton(security);
var stripe = StripePaymentOptions.Load(builder.Configuration, builder.Environment);
builder.Services.AddSingleton(stripe);
builder.Services.AddHttpClient<IStripeBillingClient, StripeBillingClient>(client =>
{
    client.BaseAddress = new Uri("https://api.stripe.com/");
    client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddSingleton<StripePaymentsService>();
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
