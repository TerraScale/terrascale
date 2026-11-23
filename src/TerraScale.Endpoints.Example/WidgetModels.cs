namespace TerraScale.Endpoints.Example;

public sealed record WidgetNotFound;

public sealed record WidgetConflict;

public readonly union CreateWidgetResult(Widget, WidgetConflict);

public readonly union GetWidgetResult(Widget, WidgetNotFound);

public sealed class WidgetStore
{
    private readonly Lock gate = new();
    private readonly Dictionary<Guid, Widget> widgets = [];

    public CreateWidgetResult Create(string name)
    {
        lock (gate)
        {
            if (widgets.Values.Any(existing => existing.Name == name))
            {
                return new WidgetConflict();
            }

            var widget = new Widget(Guid.NewGuid(), name);
            widgets[widget.Id] = widget;
            return widget;
        }
    }

    public bool NameExists(string name)
    {
        lock (gate)
        {
            return widgets.Values.Any(widget => widget.Name == name);
        }
    }

    public GetWidgetResult Find(Guid id)
    {
        lock (gate)
        {
            return widgets.TryGetValue(id, out var widget) ? widget : new WidgetNotFound();
        }
    }
}

public sealed record Widget(Guid Id, string Name);
