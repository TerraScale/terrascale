namespace TerraScale.Endpoints;

public interface IEndpoint
{
    RouteHandlerBuilder Map(IEndpointRouteBuilder routes);
}
