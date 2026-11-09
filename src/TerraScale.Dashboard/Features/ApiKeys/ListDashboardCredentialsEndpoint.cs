using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Platform.Contracts;
using TerraScale.Dashboard.Features.Databases;
using Microsoft.AspNetCore.Mvc;
using TerraScale.Dashboard.Platform.Http;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.ApiKeys
{
    public sealed class ListDashboardCredentialsEndpoint(DashboardSessions sessions, AccountService accounts, ApiKeyService keys) : EndpointWithoutRequest<IResult>
    {
        public override void Configure()
        {
            Get("/api/v1/dashboard/credentials");
            Metadata(new IntegrationEndpointGroup("integration"));
            Name("listDashboardCredentials");
            Tags("Credentials");
            Summary("List ts-auth project keys without secrets");
            Description(CursorPaging.IssuerDescription + " Keys arrive in the issuer's creation order, one bounded issuer page per request (no total is available). DatabaseId (a ULID) is the restriction; DatabaseName is its display name (Deleted database once the ID is gone). A key with DatabaseName but no DatabaseId was narrowed by name and mints no token. LastUsedAt is the last token exchange (up to five minutes stale). PreviousSecretExpiresAt is the UTC instant until which the key string before the last rotation still works; it is empty once that grace period has passed.");
            Metadata(new CursorPagingParameters());
            Metadata(new ProducesResponseTypeMetadata(200, typeof(CursorPage<ProjectCredentialMetadata>), ["application/json"]));
            Metadata(new ProducesResponseTypeMetadata(401, typeof(void)));
            Metadata(new ProducesResponseTypeMetadata(403, typeof(ProblemDetails), ["application/problem+json"]));
            Metadata(new ProducesResponseTypeMetadata(422, typeof(ProblemDetails), ["application/problem+json"]));
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

            try
            {
                var (limit, after) = CursorPaging.Read(http.Request);
                var page = await keys.ListPageAsync(context, after.Length == 0 ? null : after, limit, http.RequestAborted);
                return Results.Json(new CursorPage<ProjectCredentialMetadata>([.. page.Items.Select(ApiKeyService.CredentialMetadata)], page.NextCursor, limit),
                    ApiKeyContractJson.Default.CursorPageProjectCredentialMetadata);
            }
            catch (Exception error) when (DatabaseEndpoints.KnownFailure(error) || error is HttpRequestException or IOException or System.Text.Json.JsonException) { return DatabaseEndpoints.Failure(error); }
        }
    }
}
