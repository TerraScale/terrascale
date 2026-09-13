using TerraScale.Endpoints;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<WidgetStore>();
builder.Services.AddTerraScaleEndpoints(typeof(Program).Assembly, options =>
{
    options.Title = "TerraScale.Endpoints example";
    options.Version = "v1";
});

var app = builder.Build();
app.MapTerraScaleEndpoints();
app.MapTerraScaleOpenApi();
app.Run();

public sealed class WidgetStore
{
    private readonly Dictionary<Guid, Widget> widgets = [];

    public Widget Create(string name)
    {
        var widget = new Widget(Guid.NewGuid(), name);
        lock (this.widgets)
        {
            this.widgets[widget.Id] = widget;
        }

        return widget;
    }

    public Widget? Find(Guid id)
    {
        lock (this.widgets)
        {
            return this.widgets.GetValueOrDefault(id);
        }
    }
}

public sealed record Widget(Guid Id, string Name);
