using TerraScale.Endpoints;

namespace TerraScale.Endpoints.Example.Endpoints;

/// <summary>FastEndpoints-style request endpoint using the per-request sender.</summary>
public sealed class EchoWidgetEndpoint : Endpoint<EchoWidgetRequest>
{
    public override void Configure()
    {
        Post("/widgets/echo");
        Summary("Echoes a widget name");
        Tags("Widgets");
        Produces<EchoWidgetResponse>();
        AllowAnonymous();
    }

    public override async Task HandleAsync(EchoWidgetRequest request, CancellationToken cancellationToken)
    {
        await Send.OkAsync(new EchoWidgetResponse(request.Name));
    }
}

public sealed record EchoWidgetRequest(string Name);
public sealed record EchoWidgetResponse(string Name);
