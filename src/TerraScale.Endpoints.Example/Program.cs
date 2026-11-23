using TerraScale.Endpoints;
using TerraScale.Endpoints.Example;

var builder = WebApplication.CreateSlimBuilder(args);
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, ExampleJsonContext.Default));
builder.Services.AddSingleton<WidgetStore>();
builder.Services.AddValidation();

builder.Services.AddTerraScaleEndpoints(options =>
{
    options.Title = "TerraScale.Endpoints example";
    options.Version = "v1";
    options.MapResponse<WidgetNotFound>(_ => TypedResults.NotFound(), StatusCodes.Status404NotFound);
    options.MapResponse<WidgetConflict>(_ => TypedResults.Conflict(), StatusCodes.Status409Conflict);
});

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapTerraScaleEndpoints();
app.MapTerraScaleOpenApi();
app.Run();
