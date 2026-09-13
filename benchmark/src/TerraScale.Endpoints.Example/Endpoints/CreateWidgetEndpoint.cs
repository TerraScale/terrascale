using TerraScale.Endpoints;

namespace TerraScale.Endpoints.Example.Endpoints;

[Endpoint]
public sealed class CreateWidgetEndpoint(WidgetStore store) : Endpoint<CreateWidgetRequest, Widget>
{
    public override string Route => "/widgets";
    public override string Name => "create-widget";
    public override string Summary => "Creates a widget";
    public override string Description => "The request is bound from JSON and the response is documented in OpenAPI.";
    public override IReadOnlyList<string> Tags => ["Widgets"];

    public override Task<Widget> ExecuteAsync(CreateWidgetRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(store.Create(request.Name));
}

public sealed record CreateWidgetRequest(string Name);
