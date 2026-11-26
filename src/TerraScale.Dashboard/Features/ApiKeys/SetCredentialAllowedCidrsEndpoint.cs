using TerraScale.Dashboard.Platform.Datastar;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Databases;
using TerraScale.Dashboard.Common.UI;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.ApiKeys
{
    public sealed class SetCredentialAllowedCidrsEndpoint(DashboardSessions sessions, AccountService accounts, ApiKeyService keys, DatabaseService databases) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/api/v1/dashboard/credentials/allowed-cidrs").AuthenticateInHandler();

        public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
        {
            var http = HttpContext;
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null)
            {
                return TypedResults.Unauthorized();
            }

            var form = await FormRequests.ReadAsync(http, http.RequestAborted);
            if (!Csrf.Validate(http, form))
            {
                return RequestError.FormExpired(http, "/api-keys", TypedResults.BadRequest("Invalid form token."));
            }

            var cidrs = ApiKeyService.ParseCidrs(form["allowedCidrs"].ToString());
            var keyId = form["keyId"].ToString();
            try
            {
                var key = await keys.SetAllowedCidrsAsync(context, keyId, cidrs, http.RequestAborted);
                if (!DatabaseEndpoints.WantsHtml(http))
                {
                    return TypedResults.Json(ApiKeyService.CredentialMetadata(key), ApiKeyContractJson.Default.ProjectCredentialMetadata);
                }
            }
            catch (Exception error) when (DatabaseEndpoints.KnownFailure(error) || error is HttpRequestException)
            { return DatabaseEndpoints.WantsHtml(http) ? await ApiKeyPages.PageAsync(http, context, keys, databases, DatabaseEndpoints.Message(error), status: DatabaseEndpoints.Status(error), draftKeyCidrs: keyId, draftAllowedCidrs: form["allowedCidrs"].ToString()) : DatabaseEndpoints.Failure(error); }
            if (http.Request.IsDatastarRequest)
            {
                await ApiKeyPages.DetailsAsync(http, context, keys, databases, keyId);
                return TypedResults.Empty;
            }

            return DatabaseEndpoints.WantsHtml(http) ? HttpRedirects.SeeOther("/api-keys/" + keyId) : TypedResults.NoContent();
        }
    }
}
