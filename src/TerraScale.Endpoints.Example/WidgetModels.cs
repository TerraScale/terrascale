namespace TerraScale.Endpoints.Example;

public sealed class WidgetStore
{
    private readonly Dictionary<Guid, Widget> widgets = [];

    /// <summary>Creates a widget unless the name is taken; the check and insert happen under one lock.</summary>
    public bool TryCreate(string name, out Widget widget)
    {
        lock (widgets)
        {
            if (widgets.Values.Any(existing => existing.Name == name))
            {
                widget = null!;
                return false;
            }

            widget = new Widget(Guid.NewGuid(), name);
            widgets[widget.Id] = widget;
            return true;
        }
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
