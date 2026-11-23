using System.Text.Json.Serialization;
using TerraBase.Configuration;
using TerraBase.Hosting;
using TerraBase.Storage;
using TerraScale.Endpoints;

namespace TerraBase.Features.Clock;

public sealed record ClockBody(string Region, string Machine, long WallMs, long HlcWallMs, int HlcLogical, int HlcNode);

[JsonSourceGenerationOptions(System.Text.Json.JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(ClockBody))]
internal sealed partial class ClockJsonContext : JsonSerializerContext;

internal sealed class ClockEndpoint(SqliteStorageEngine engine, TerraBaseOptions options, TimeProvider time) : EndpointWithoutRequest<ClockBody>
{
    public override void Configure()
    {
        Get("/v1/sync/clock");
        Name("clock");
        Summary("This node's wall clock and hybrid logical clock");
        Description("Peers compare clocks with it; the HLC is the one stamping commits on this node.");
        Tags("Internal");
        AllowAnonymous();
        Metadata(ListenerMetadata.Internal);
    }

    public override ValueTask<ClockBody> HandleAsync(CancellationToken cancellationToken)
    {
        var hlc = engine.Clock.Current;
        return ValueTask.FromResult(new ClockBody(options.Region, options.MachineId, time.GetUtcNow().ToUnixTimeMilliseconds(), hlc.WallMs, hlc.Logical, hlc.NodeId));
    }
}
