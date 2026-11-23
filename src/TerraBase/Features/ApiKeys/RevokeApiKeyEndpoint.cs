using TerraBase.Features.Callers;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.ApiKeys;

internal sealed class RevokeApiKeyEndpoint(ApiKeyStore keys) : Endpoint<RevokeApiKeyInput, Result<Done, Forbidden, NotFound, Unavailable>>
{
    public override void Configure()
    {
        Delete("/admin/api-keys/{key}");
        Name("revokeApiKey");
        Summary("Revokes an API key");
        Tags("Administration");
        AllowAnonymous();
        Metadata(ListenerMetadata.Internal);
    }

    public override async ValueTask<Result<Done, Forbidden, NotFound, Unavailable>> HandleAsync(RevokeApiKeyInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!PlanFlow.IsOperator(HttpContext.Caller, "admin"))
        {
            return new Forbidden("revoking API keys needs the admin grant", "forbidden_caller");
        }

        return await keys.RevokeAsync(request.Key, cancellationToken) switch
        {
            Done done => done,
            NotFound missing => missing,
            Unavailable unavailable => unavailable,
        };
    }
}
