using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Platform.Contracts;
using Microsoft.AspNetCore.Http.Metadata;
using TerraScale.Dashboard.Features.Databases;
using Microsoft.AspNetCore.Mvc;
using TerraScale.Dashboard.Common.UI;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.ApiKeys
{
    public sealed class RevokeDashboardCredentialByIdEndpoint(DashboardSessions sessions, AccountService accounts, ApiKeyService keys, DatabaseService databases) : EndpointWithoutRequest<IResult>
    {
        public override void Configure()
        {
            Post("/api/v1/dashboard/credentials/{keyId}/revoke");
            Metadata(new IntegrationEndpointGroup("integration"));
            Name("revokeDashboardCredentialById");
            Tags("Credentials");
            Summary("Revoke a project key");
            Description("Deletes the key at ts-auth: token requests then fail with invalid_client. Revoking stops this key everywhere within 2 seconds: new requests are refused and open connections (including Postgres) are closed within 3 seconds. A failed revoke call is re-checked, so 204/303 means the key is gone. Idempotent: revoking a key that is already revoked or absent from this project succeeds.");
            Metadata(new AcceptsMetadata(["application/x-www-form-urlencoded"], typeof(RevokeProjectCredentialRequest), false));
            Metadata(new ProducesResponseTypeMetadata(204, typeof(void)));
            Metadata(new ProducesResponseTypeMetadata(303, typeof(void)));
            Metadata(new ProducesResponseTypeMetadata(400, typeof(string), ["application/json"]));
            Metadata(new ProducesResponseTypeMetadata(401, typeof(void)));
            Metadata(new ProducesResponseTypeMetadata(403, typeof(ProblemDetails), ["application/problem+json"]));
            Metadata(new ProducesResponseTypeMetadata(404, typeof(ProblemDetails), ["application/problem+json"]));
            Metadata(new ProducesResponseTypeMetadata(503, typeof(ProblemDetails), ["application/problem+json"]));
        }

        public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
        {
            var http = HttpContext;
            var keyId = http.Request.RouteValues["keyId"]?.ToString() ?? "";
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null)
            {
                return Results.Unauthorized();
            }

            var form = await FormRequests.ReadAsync(http, http.RequestAborted);
            if (!Csrf.Validate(http, form))
            {
                return RequestError.FormExpired(http, "/api-keys", Results.BadRequest("Invalid form token."));
            }

            try { await ApiKeyPages.RecordRevokeAsync(http, context, accounts, keys, keyId); }
            catch (Exception error) when (DatabaseEndpoints.KnownFailure(error)) { return DatabaseEndpoints.WantsHtml(http) ? await ApiKeyPages.PageAsync(http, context, keys, databases, DatabaseEndpoints.Message(error), status: DatabaseEndpoints.Status(error)) : DatabaseEndpoints.Failure(error); }
            return DatabaseEndpoints.WantsHtml(http) ? HttpRedirects.SeeOther("/api-keys") : Results.NoContent();
        }
    }
}
