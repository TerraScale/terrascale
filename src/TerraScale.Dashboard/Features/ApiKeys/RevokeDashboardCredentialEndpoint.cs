using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Platform.Contracts;
using Microsoft.AspNetCore.Http.Metadata;
using TerraScale.Dashboard.Features.Databases;
using Microsoft.AspNetCore.Mvc;
using TerraScale.Dashboard.Common.UI;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.ApiKeys
{
    public sealed class RevokeDashboardCredentialEndpoint(DashboardSessions sessions, AccountService accounts, ApiKeyService keys, DatabaseService databases) : EndpointWithoutRequest<IResult>
    {
        public override void Configure()
        {
            Post("/api/v1/dashboard/credentials/revoke");
            Metadata(new IntegrationEndpointGroup("integration"));
            Name("revokeDashboardCredential");
            Tags("Credentials");
            Summary("Revoke a key selected by its form ID");
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

            try { await ApiKeyPages.RecordRevokeAsync(http, context, accounts, keys, form["keyId"].ToString()); }
            catch (Exception error) when (DatabaseEndpoints.KnownFailure(error)) { return DatabaseEndpoints.WantsHtml(http) ? await ApiKeyPages.PageAsync(http, context, keys, databases, DatabaseEndpoints.Message(error), status: DatabaseEndpoints.Status(error)) : DatabaseEndpoints.Failure(error); }
            return DatabaseEndpoints.WantsHtml(http) ? HttpRedirects.SeeOther("/api-keys" + http.Request.QueryString) : Results.NoContent();
        }
    }
}
