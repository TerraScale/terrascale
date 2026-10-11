namespace TerraScale.Endpoints.Example.Endpoints;

/// <summary>Binds <c>{id}</c> from the route into the request record; no BindAsync override needed.</summary>
public sealed class GetWidgetEndpoint(WidgetStore store) : Endpoint<GetWidgetRequest, IResult>
{
    public override void Configure()
    {
        Get("/widgets/{id:guid}");
        Name("get-widget");
        Summary("Gets a widget by id");
        Tags("Widgets");
        Produces<Widget>();
        Produces(StatusCodes.Status404NotFound);
    }

    public override ValueTask<IResult> HandleAsync(GetWidgetRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IResult>(store.Find(request.Id) is { } widget ? TypedResults.Ok(widget) : TypedResults.NotFound());
}

public sealed record GetWidgetRequest(Guid Id);
