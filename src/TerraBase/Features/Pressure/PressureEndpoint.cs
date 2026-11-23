using System.Text.Json.Serialization;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Pressure;

[JsonSourceGenerationOptions(System.Text.Json.JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(PressureBody))]
internal sealed partial class PressureJsonContext : JsonSerializerContext;

internal sealed class PressureEndpoint(PressureSignals pressure) : EndpointWithoutRequest<PressureBody>
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

    public override ValueTask<PressureBody> HandleAsync(CancellationToken cancellationToken) => ValueTask.FromResult(pressure.Sample());
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
