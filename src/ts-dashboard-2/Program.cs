
using TerraScale.Dashboard2;
using TerraScale.Endpoints;

var builder = WebApplication.CreateBuilder(args);
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
