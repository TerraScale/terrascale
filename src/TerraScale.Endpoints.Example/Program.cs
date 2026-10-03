using TerraScale.Endpoints;
using TerraScale.Endpoints.Example;

var builder = WebApplication.CreateSlimBuilder(args);
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, ExampleJsonContext.Default));
builder.Services.AddSingleton<WidgetStore>();

// Generated at compile time: registers every endpoint in this project, no reflection scanning.
builder.Services.AddTerraScaleEndpoints(options =>
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
