using TerraScale.Endpoints;
using TerraScale.Endpoints.Example;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<WidgetStore>();
builder.Services.AddTerraScaleEndpoints(typeof(Program).Assembly, options =>
{
    options.Title = "TerraScale.Endpoints example";
    options.Version = "v1";
});
var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapTerraScaleEndpoints();
app.MapTerraScaleOpenApi();
app.Run();
