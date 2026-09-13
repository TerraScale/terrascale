using TerraScale.Endpoints;
namespace TerraScale.Endpoints.Example.Endpoints;

public sealed class CreateWidgetEndpoint(WidgetStore store) : Endpoint<CreateWidgetRequest, Widget>
{
    public override void Configure() { Post("/widgets"); Name("create-widget"); Summary("Creates a widget"); Description("JSON request and response with DI."); Tags("Widgets"); }
    public override Task<Widget> HandleAsync(CreateWidgetRequest request, CancellationToken cancellationToken) => Task.FromResult(store.Create(request.Name));
}
public sealed record CreateWidgetRequest(string Name);
