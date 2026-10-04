namespace TerraScale.Endpoints.Example;

public sealed class WidgetStore
{
    private readonly Dictionary<Guid, Widget> widgets = [];

    public Widget Create(string name)
    {
        var widget = new Widget(Guid.NewGuid(), name);
        lock (widgets)
        {
            widgets[widget.Id] = widget;
        }

        return widget;
    }

    public bool NameExists(string name)
    {
        lock (widgets)
        {
            return widgets.Values.Any(widget => widget.Name == name);
        }
    }

    public Widget? Find(Guid id)
    {
        lock (widgets)
        {
            return widgets.GetValueOrDefault(id);
        }
    }
}

public sealed record Widget(Guid Id, string Name);
