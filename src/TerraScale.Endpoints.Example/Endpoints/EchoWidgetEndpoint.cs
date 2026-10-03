namespace TerraScale.Endpoints.Example.Endpoints;

/// <summary>FastEndpoints-style request/response endpoint.</summary>
public sealed class EchoWidgetEndpoint : Endpoint<EchoWidgetRequest, EchoWidgetResponse>
{
    public override void Configure()
    {
        Post("/widgets/echo");
        Summary("Echoes a widget name");
        Tags("Widgets");
        AllowAnonymous();
    }

    public override ValueTask<EchoWidgetResponse> HandleAsync(EchoWidgetRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new EchoWidgetResponse(request.Name));
}

public sealed record EchoWidgetRequest(string Name);
public sealed record EchoWidgetResponse(string Name);
