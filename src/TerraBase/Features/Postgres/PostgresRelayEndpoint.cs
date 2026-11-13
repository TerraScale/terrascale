using System.Net;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
using TerraBase.Errors;
using TerraBase.Hosting;
using TerraBase.Features.Regions;
using TerraScale.Endpoints;

namespace TerraBase.Features.Postgres;

internal sealed class PostgresRelayEndpoint(PostgresConnectionHandler handler) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post(PostgresRelay.Path);
        Name("relayPostgresSession");
        Summary("Serves a Postgres wire session relayed by another region");
        Description("Internal listener only, behind the peer credential; 404 on the public listener. The request body is the client's Postgres stream, "
            + "starting with its startup and password messages, and the response body is this node's answer, both streamed for the whole session. "
            + "X-TerraBase-Postgres-Client names the client address, which IP allowlists check.");
        Tags("Administration");
        AllowAnonymous();
        Metadata(ListenerMetadata.Internal);
        Produces<byte[]>(StatusCodes.Status200OK, "application/octet-stream");
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (HttpContext.Request.Headers[PostgresRelay.ClientHeader] is not [{ } address] || !IPAddress.TryParse(address, out var client))
        {
            return ErrorResponse.From(new Validation("X-TerraBase-Postgres-Client names one address"));
        }

        if (HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
        {
            size.MaxRequestBodySize = null;
        }

        if (HttpContext.Features.Get<IHttpMinRequestBodyDataRateFeature>() is { } requestRate)
        {
            requestRate.MinDataRate = null;
        }

        if (HttpContext.Features.Get<IHttpMinResponseDataRateFeature>() is { } responseRate)
        {
            responseRate.MinDataRate = null;
        }

        HttpContext.Response.ContentType = "application/octet-stream";
        await HttpContext.Response.StartAsync(cancellationToken).ConfigureAwait(false);
        await handler.ServeRelayedAsync(HttpContext.Request.BodyReader, HttpContext.Response.BodyWriter, client, ProxyRequestBudget.For(HttpContext), HttpContext.Abort, cancellationToken).ConfigureAwait(false);
        return Results.Empty;
    }
}
