using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Platform.Contracts;
using Microsoft.AspNetCore.Http.Metadata;
using TerraScale.Dashboard.Features.Databases;
using Microsoft.AspNetCore.Mvc;
using TerraScale.Dashboard.Common.UI;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.ApiKeys
{
    public sealed class RotateDashboardCredentialEndpoint(DashboardSessions sessions, AccountService accounts, ApiKeyService keys, DatabaseService databases) : EndpointWithoutRequest<IResult>
    {
        public override void Configure()
        {
            Post("/api/v1/dashboard/credentials/rotate");
            Metadata(new IntegrationEndpointGroup("integration"));
            Name("rotateDashboardCredential");
            Tags("Credentials");
            Summary("Rotate a project key and reveal its new secret once");
            Description("Replaces the key's secret at ts-auth with the original user session; the key ID, name, scopes and database restriction stay the same. previousSecretTtl (0 to 86400 seconds, default 0) keeps the old secret working for that grace period. With 0, the old secret and its tokens stop everywhere within 2 seconds; open connections (including Postgres) close within 3 seconds. With grace, these bounds apply from its end. While a grace period is active another rotation answers 409. A browser form post re-renders the API keys page with the new secret instead of JSON. Secrets are never persisted by the dashboard.");
            Metadata(new AcceptsMetadata(["application/x-www-form-urlencoded"], typeof(RotateProjectCredentialRequest), false));
            Metadata(new ProducesResponseTypeMetadata(200, typeof(ProjectApiKeySecret), ["application/json"]));
            Metadata(new ProducesResponseTypeMetadata(400, typeof(string), ["application/json"]));
            Metadata(new ProducesResponseTypeMetadata(401, typeof(void)));
            Metadata(new ProducesResponseTypeMetadata(403, typeof(ProblemDetails), ["application/problem+json"]));
            Metadata(new ProducesResponseTypeMetadata(404, typeof(ProblemDetails), ["application/problem+json"]));
            Metadata(new ProducesResponseTypeMetadata(409, typeof(ProblemDetails), ["application/problem+json"]));
            Metadata(new ProducesResponseTypeMetadata(422, typeof(ProblemDetails), ["application/problem+json"]));
            Metadata(new ProducesResponseTypeMetadata(503, typeof(ProblemDetails), ["application/problem+json"]));
        }

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(RotateAsync(HttpContext, sessions, accounts, keys, databases));

        public static async Task<IResult> RotateAsync(HttpContext http, DashboardSessions sessions, AccountService accounts, ApiKeyService keys, DatabaseService databases)
        {
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null)
            {
                return Results.Unauthorized();
            }

            var form = await FormRequests.ReadAsync(http, http.RequestAborted);
            if (!StepUpFlow.IsResuming(http) && !Csrf.Validate(http, form))
            {
                return Results.BadRequest("Invalid form token.");
            }

            try
            {
                if (!context.CanManageKeys)
                {
                    throw new AccountAccessException("Project member access is required.");
                }

                var ttl = form["previousSecretTtl"].ToString();
                var previousSecretTtl = 0;
                if (ttl.Length > 0 && !int.TryParse(ttl, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out previousSecretTtl))
                {
                    throw new AccountAccessException("The old secret's grace period must be between 0 and 86400 seconds.", 422);
                }

                var intent = StepUpFlow.Intent(http, context, "rotate_api_key");
                if (intent is null)
                {
                    return await StepUpFlow.StartAsync(http, context, "rotate_api_key", "ts-auth");
                }

                var credential = await keys.RotateAsync(context, form["keyId"].ToString(), previousSecretTtl, http.RequestAborted, intent.Challenge.Proof);
                return DatabaseEndpoints.WantsHtml(http) ? await ApiKeyPages.PageAsync(http, context, keys, databases, revealed: credential) : Results.Json(new ProjectApiKeySecret(ApiKeyService.Metadata(credential.Key), credential.Secret), ApiKeyContractJson.Default.ProjectApiKeySecret);
            }
            catch (Exception error) when (DatabaseEndpoints.KnownFailure(error)) { return DatabaseEndpoints.WantsHtml(http) ? await ApiKeyPages.PageAsync(http, context, keys, databases, DatabaseEndpoints.Message(error), status: DatabaseEndpoints.Status(error)) : DatabaseEndpoints.Failure(error); }
        }
    }
}
