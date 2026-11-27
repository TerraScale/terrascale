using System.Text.Json.Serialization;
using TerraBase.Features.Callers;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Pressure;

[JsonSourceGenerationOptions(System.Text.Json.JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(PressureBody))]
internal sealed partial class PressureJsonContext : JsonSerializerContext;

internal readonly union PressureResponse(PressureBody, CallerForbidden);

internal sealed class PressureEndpoint(PressureSignals pressure) : EndpointWithoutRequest<PressureResponse>
{
    public override void Configure()
    {
        Get("/v1/admin/node/pressure");
        Name("nodePressure");
        Summary("This node's load: connections, requests in flight, CPU, memory and disk");
        Tags("Administration");
        AllowAnonymous();
        Metadata(ListenerMetadata.Internal);
    }

    public override ValueTask<PressureResponse> HandleAsync(CancellationToken cancellationToken) => ValueTask.FromResult<PressureResponse>(
        CallerAuthenticationExtensions.OperatesNode(HttpContext.Caller)
            ? pressure.Sample()
            : new CallerForbidden("node pressure needs the peer credential or an operator admin grant", "forbidden_caller"));
}

internal static class PressureExtensions
{
    extension(WebApplication app)
    {
        public WebApplication UseTerraBasePressure()
        {
            var pressure = app.Services.GetRequiredService<PressureSignals>();
            app.Use(async (context, next) =>
            {
                using var lease = pressure.Request();
                await next(context);
            });
            return app;
        }
    }
}
