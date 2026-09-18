
using TerraScale.Dashboard2;
using TerraScale.Endpoints;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<DashboardStore>();
builder.Services.AddTerraScaleEndpoints(typeof(Program).Assembly, options =>
{
    options.Title = "TerraScale Dashboard";
    options.Version = "v1";
});

var app = builder.Build();

app.UseStaticFiles();
app.UseMiddleware<DashboardAuthMiddleware>();

app.MapTerraScaleEndpoints();
app.MapTerraScaleOpenApi();

app.Run();
