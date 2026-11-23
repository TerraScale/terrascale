using TerraBase.Features.Callers;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.ApiKeys;

internal readonly union RevokeApiKeyResponse(Completed, CallerForbidden, ResourceNotFound, BackendUnavailable);

internal sealed class RevokeApiKeyEndpoint(ApiKeyStore keys) : Endpoint<RevokeApiKeyInput, RevokeApiKeyResponse>
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

    public override async ValueTask<RevokeApiKeyResponse> HandleAsync(RevokeApiKeyInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!PlanFlow.IsOperator(HttpContext.Caller, "admin"))
        {
            return new CallerForbidden("revoking API keys needs the admin grant", "forbidden_caller");
        }

        var outcome = await keys.RevokeAsync(request.Key, cancellationToken);
        return outcome switch
        {
            Completed done => done,
            ResourceNotFound missing => missing,
            BackendUnavailable unavailable => unavailable,
        };
    }
}
