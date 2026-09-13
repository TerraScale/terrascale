using TerraScale.Endpoints;

namespace TerraScale.Endpoints.Example.Endpoints;

[Endpoint]
public sealed class CreateWidgetEndpoint(WidgetStore store) : Endpoint<CreateWidgetRequest, Widget>
{
    public override void Configure(EndpointDefinition definition) => definition
        .Post("/widgets")
        .WithName("create-widget")
        .WithSummary("Creates a widget")
        .WithDescription("The request is bound from JSON and the response is documented in OpenAPI.")
        .WithTags("Widgets");

    public override Task<Widget> ExecuteAsync(CreateWidgetRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(store.Create(request.Name));
}

public sealed record CreateWidgetRequest(string Name);
