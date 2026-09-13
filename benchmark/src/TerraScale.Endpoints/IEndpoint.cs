namespace TerraScale.Endpoints;

/// <summary>A discoverable endpoint that maps exactly one HTTP route.</summary>
public interface IEndpoint
{
    /// <summary>Maps this endpoint onto the application.</summary>
    /// <param name="routes">The application route builder.</param>
    RouteHandlerBuilder Map(IEndpointRouteBuilder routes);
}
