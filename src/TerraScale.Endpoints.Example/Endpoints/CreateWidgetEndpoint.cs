namespace TerraScale.Endpoints.Example.Endpoints;

public sealed class CreateWidgetEndpoint(WidgetStore store) : Endpoint<CreateWidgetRequest, Widget>
{
    public override void Configure() { Post("/widgets"); Name("create-widget"); Summary("Creates a widget"); Description("JSON request and response with DI."); Tags("Widgets"); }
    public override ValueTask<Widget> HandleAsync(CreateWidgetRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(store.Create(request.Name));
}
public sealed record CreateWidgetRequest(string Name);
