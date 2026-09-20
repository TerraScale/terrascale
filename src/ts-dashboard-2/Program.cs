
using TerraScale.Dashboard2;
using TerraScale.Endpoints;

var builder = WebApplication.CreateBuilder(args);
var security = DashboardSecurityOptions.Load(builder.Configuration, builder.Environment);
builder.Services.AddSingleton(security);
if (!security.DemoMode)
{
    builder.Services.AddHttpClient<LiveIdentityProvider>(client => client.Timeout = TimeSpan.FromSeconds(5));
    builder.Services.AddSingleton<DashboardStateRepository>();
    builder.Services.AddSingleton<LiveAuthSessionService>();
    builder.Services.AddSingleton<TerrabaseDataPlane>();
}
builder.Services.AddResponseCompression(); // Brotli + Gzip, default MIME types
builder.Services.AddSingleton<DashboardStore>();
builder.Services.AddSingleton<TerraBaseBackend>();
builder.Services.AddTerraScaleEndpoints(typeof(Program).Assembly, options =>
{
    options.Title = "TerraScale Dashboard";
    options.Version = "v1";
});

var app = builder.Build();

app.UseResponseCompression();
app.UseStaticFiles();
app.UseMiddleware<DashboardAuthMiddleware>();

app.MapTerraScaleEndpoints();
app.MapTerraScaleOpenApi();

app.Run();

/// <summary>Exposes the top-level Program class to WebApplicationFactory-driven tests.</summary>
public partial class Program;
