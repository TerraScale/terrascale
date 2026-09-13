using TerraScale.Endpoints;
namespace TerraScale.Endpoints.Example.Endpoints;

public sealed class GetWidgetEndpoint(WidgetStore store) : IEndpoint
{
    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes) => routes.MapGet("/widgets/{id:guid}", (Guid id) => store.Find(id) is { } widget ? Results.Ok(widget) : Results.NotFound()).WithName("get-widget").WithSummary("Gets a widget by id").WithTags("Widgets").Produces<Widget>().Produces(StatusCodes.Status404NotFound);
}
