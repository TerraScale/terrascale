using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Platform.Contracts;
using Microsoft.AspNetCore.Http.Metadata;
using TerraScale.Dashboard.Features.Databases;
using Microsoft.AspNetCore.Mvc;
using TerraScale.Dashboard.Common.UI;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.ApiKeys
{
    public sealed class CreateDashboardCredentialEndpoint(DashboardSessions sessions, AccountService accounts, ApiKeyService keys, DatabaseService databases) : EndpointWithoutRequest<IResult>
    {
        public override void Configure()
        {
            Post("/api/v1/dashboard/credentials/{kind}");
            Metadata(new IntegrationEndpointGroup("integration"));
            Name("createDashboardCredential");
            Tags("Credentials");
            Summary("Create a project key and reveal its secret once");
            Description("Uses the original user session at ts-auth. " + ApiKeyService.ScopeDescription + " expiresInDays is 0 (default, the key works until revoked), 30, 90 or 365. allowedCidrs (up to 32 IPs or CIDR ranges, comma or newline separated; empty allows any IP) limits where the key's requests may come from. Kind is key (api-key also accepted); services exchange the key with grant_type=client_credentials at ts-auth. Secrets are never persisted by the dashboard.");
            Metadata(new AcceptsMetadata(["application/x-www-form-urlencoded"], typeof(CreateProjectCredentialRequest), false));
            Metadata(new ProducesResponseTypeMetadata(StatusCodes.Status201Created, typeof(ProjectApiKeySecret), ["application/json"]));
            Metadata(new ProducesResponseTypeMetadata(StatusCodes.Status200OK, typeof(void)));
            Metadata(new ProducesResponseTypeMetadata(400, typeof(string), ["application/json"]));
            Metadata(new ProducesResponseTypeMetadata(401, typeof(void)));
            Metadata(new ProducesResponseTypeMetadata(403, typeof(ProblemDetails), ["application/problem+json"]));
            Metadata(new ProducesResponseTypeMetadata(422, typeof(ProblemDetails), ["application/problem+json"]));
            Metadata(new ProducesResponseTypeMetadata(503, typeof(ProblemDetails), ["application/problem+json"]));
        }

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(CreateAsync(HttpContext, HttpContext.Request.RouteValues["kind"]?.ToString() ?? "", sessions, accounts, keys, databases));

        public static async Task<IResult> CreateAsync(HttpContext http, string kind, DashboardSessions sessions, AccountService accounts, ApiKeyService keys, DatabaseService databases)
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

                if (kind is not ("key" or "api-key"))
                {
                    throw new AccountAccessException("Kind must be key.", 422);
                }

                var expires = form["expiresInDays"].ToString();
                var expiresInDays = 0;
                if (expires.Length > 0 && !int.TryParse(expires, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out expiresInDays))
                {
                    throw new AccountAccessException("Choose no expiry, 30 days, 90 days, or 1 year.", 422);
                }

                _ = keys.PrepareCreate(context, kind, form["name"].ToString(), form["databaseId"].ToString(),
                    form["scope"].Concat(form["scopes"]).SelectMany(value => (value ?? "").Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)),
                    expiresInDays, ApiKeyService.ParseCidrs(form["allowedCidrs"].ToString()));
                var intent = StepUpFlow.Intent(http, context, "create_api_key");
                if (intent is null)
                {
                    return await StepUpFlow.StartAsync(http, context, "create_api_key", "ts-auth");
                }

                var credential = await keys.CreateAsync(context, kind, form["name"].ToString(), form["databaseId"].ToString(), form["scope"].Concat(form["scopes"]).SelectMany(value => (value ?? "").Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)), expiresInDays, ApiKeyService.ParseCidrs(form["allowedCidrs"].ToString()), http.RequestAborted, intent.Challenge.Proof);
                await keys.RecordAsync(accounts, context, credential.Key.Id, true, http.RequestAborted);
                return DatabaseEndpoints.WantsHtml(http) ? await ApiKeyPages.PageAsync(http, context, keys, databases, revealed: credential) : Results.Json(new ProjectApiKeySecret(ApiKeyService.Metadata(credential.Key), credential.Secret), ApiKeyContractJson.Default.ProjectApiKeySecret, statusCode: 201);
            }
            catch (Exception error) when (DatabaseResults.KnownFailure(error) || error is HttpRequestException)
            {
                return DatabaseResults.WantsHtml(http) ? await ApiKeyPages.PageAsync(http, context, keys, databases, DatabaseResults.Message(error), kind, form["name"].ToString(), status: DatabaseResults.Status(error), draftScopes: form["scopes"].ToString(), draftDatabaseId: form["databaseId"].ToString(), draftExpiresInDays: form["expiresInDays"].ToString(), draftAllowedCidrs: form["allowedCidrs"].ToString()) : DatabaseResults.Failure(error);
            }
        }
    }
}
