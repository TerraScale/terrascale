namespace TerraScale.Endpoints.Example.Endpoints;

public sealed class GetWidgetEndpoint(WidgetStore store) : Endpoint<GetWidgetRequest, GetWidgetResult>
{
    public override void Configure()
    {
        AllowAnonymous();
        Get("/widgets/{id:guid}");
        Name("get-widget");
        Summary("Gets a widget by id");
        Tags("Widgets");
    }

    public override ValueTask<GetWidgetResult> HandleAsync(GetWidgetRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(store.Find(request.Id));
}

public sealed record GetWidgetRequest(Guid Id);
