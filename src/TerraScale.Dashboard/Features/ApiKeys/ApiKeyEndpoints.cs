using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Databases;
using TerraScale.Dashboard.Common.UI;

namespace TerraScale.Dashboard.Features.ApiKeys
{
    public static class ApiKeyEndpoints
    {
        public static IEndpointRouteBuilder MapApiKeyFeatures(this IEndpointRouteBuilder endpoints)
        {
            endpoints.MapGet("/api-keys", async (HttpContext http, DashboardSessions sessions, AccountService accounts, ApiKeyService keys) =>
            {
                var context = await AccountRequest.RequireAsync(http, sessions, accounts);
                return context is null ? AccountRequest.LoginRedirect(http) : await PageAsync(http, context, keys);
            });
            endpoints.MapGet("/api/v1/dashboard/credentials", async (HttpContext http, DashboardSessions sessions, AccountService accounts, ApiKeyService keys) =>
            {
                var context = await AccountRequest.RequireAsync(http, sessions, accounts);
                if (context is null)
                {
                    return Results.Unauthorized();
                }

                try
                {
                    var (limit, after) = CursorPaging.Read(http.Request);
                    return Results.Json(CursorPaging.Page((await keys.ListAsync(context, http.RequestAborted)).Select(ApiKeyService.CredentialMetadata),
                        key => key.KeyId, CursorPaging.Scope(context, "credentials", "", "", "", ""), limit, after));
                }
                catch (Exception error) when (DatabaseEndpoints.KnownFailure(error) || error is HttpRequestException or IOException or System.Text.Json.JsonException) { return DatabaseEndpoints.Failure(error); }
            }).WithGroupName("integration").WithName("listDashboardCredentials").WithTags("Credentials")
                .WithSummary("List ts-auth project keys without secrets")
                .WithDescription(CursorPaging.Description + " Upstream uses 100 keys/page; traversal fails at 100 pages instead of truncating. DatabaseId (a ULID) is the restriction; DatabaseName is its display name (Deleted database once the ID is gone). A key with DatabaseName but no DatabaseId was narrowed by name and mints no token. LastUsedAt is the last token exchange (up to five minutes stale).")
                .WithCursorContract().Produces<CursorPage<ProjectCredentialMetadata>>().Produces(401).ProducesProblem(403).ProducesProblem(422).ProducesProblem(503);
            endpoints.MapPost("/api/v1/dashboard/credentials/{kind}", CreateAsync).WithGroupName("integration").WithName("createDashboardCredential").WithTags("Credentials")
                .WithSummary("Create a project key and reveal its secret once")
                .WithDescription("Uses the original user session at ts-auth. " + ApiKeyService.ScopeDescription + " expiresInDays is 0 (default, the key works until revoked), 30, 90 or 365. allowedCidrs (up to 32 IPs or CIDR ranges, comma or newline separated; empty allows any IP) limits where the key's requests may come from. Kind is key (api-key also accepted); services exchange the key with grant_type=client_credentials at ts-auth. Secrets are never persisted by the dashboard.")
                .Accepts<CreateProjectCredentialRequest>("application/x-www-form-urlencoded")
                .Produces<ProjectApiKeySecret>(201).Produces(200, contentType: "text/html").Produces<string>(400).Produces(401).ProducesProblem(403).ProducesProblem(422).ProducesProblem(503);
            endpoints.MapPost("/api/v1/dashboard/credentials/{keyId}/revoke", async (HttpContext http, string keyId, DashboardSessions sessions, AccountService accounts, ApiKeyService keys) =>
            {
                var context = await AccountRequest.RequireAsync(http, sessions, accounts);
                if (context is null)
                {
                    return Results.Unauthorized();
                }

                var form = await http.Request.ReadFormAsync(http.RequestAborted);
                if (!Csrf.Validate(http, form))
                {
                    return Results.BadRequest("Invalid form token.");
                }

                try { await keys.RevokeAsync(context, keyId, http.RequestAborted); await keys.RecordAsync(accounts, context, keyId, false, http.RequestAborted); }
                catch (Exception error) when (DatabaseEndpoints.KnownFailure(error)) { return DatabaseEndpoints.WantsHtml(http) ? await PageAsync(http, context, keys, DatabaseEndpoints.Message(error), status: DatabaseEndpoints.Status(error)) : DatabaseEndpoints.Failure(error); }
                return DatabaseEndpoints.WantsHtml(http) ? HttpRedirects.SeeOther("/api-keys") : Results.NoContent();
            }).WithGroupName("integration").WithName("revokeDashboardCredentialById").WithTags("Credentials")
                .WithSummary("Revoke a project key")
                .WithDescription("Deletes the key at ts-auth: token requests then fail with invalid_client. Revoking stops this key everywhere within 2 seconds: new requests are refused and open connections (including Postgres) are closed within 3 seconds. A failed revoke call is re-checked, so 204/303 means the key is gone.")
                .Accepts<RevokeProjectCredentialRequest>("application/x-www-form-urlencoded")
                .Produces(204).Produces(303).Produces<string>(400).Produces(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(503);
            endpoints.MapPost("/api/v1/dashboard/credentials/revoke", async (HttpContext http, DashboardSessions sessions, AccountService accounts, ApiKeyService keys) =>
            {
                var context = await AccountRequest.RequireAsync(http, sessions, accounts);
                if (context is null)
                {
                    return Results.Unauthorized();
                }

                var form = await http.Request.ReadFormAsync(http.RequestAborted);
                if (!Csrf.Validate(http, form))
                {
                    return Results.BadRequest("Invalid form token.");
                }

                try { await keys.RevokeAsync(context, form["keyId"].ToString(), http.RequestAborted); await keys.RecordAsync(accounts, context, form["keyId"].ToString(), false, http.RequestAborted); }
                catch (Exception error) when (DatabaseEndpoints.KnownFailure(error)) { return DatabaseEndpoints.WantsHtml(http) ? await PageAsync(http, context, keys, DatabaseEndpoints.Message(error), status: DatabaseEndpoints.Status(error)) : DatabaseEndpoints.Failure(error); }
                return DatabaseEndpoints.WantsHtml(http) ? HttpRedirects.SeeOther("/api-keys" + http.Request.QueryString) : Results.NoContent();
            }).WithGroupName("integration").WithName("revokeDashboardCredential").WithTags("Credentials")
                .WithSummary("Revoke a key selected by its form ID")
                .Accepts<RevokeProjectCredentialRequest>("application/x-www-form-urlencoded")
                .Produces(204).Produces(303).Produces<string>(400).Produces(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(503);
            endpoints.MapPost("/api/v1/dashboard/credentials/rotate", RotateAsync).WithGroupName("integration").WithName("rotateDashboardCredential").WithTags("Credentials")
                .WithSummary("Rotate a project key and reveal its new secret once")
                .WithDescription("Replaces the key's secret at ts-auth with the original user session; the key ID, name, scopes and database restriction stay the same. previousSecretTtl (0 to 86400 seconds, default 0) keeps the old secret working for that grace period. With 0, the old secret and its tokens stop everywhere within 2 seconds; open connections (including Postgres) close within 3 seconds. With grace, these bounds apply from its end. While a grace period is active another rotation answers 409. A browser form post re-renders the API keys page with the new secret instead of JSON. Secrets are never persisted by the dashboard.")
                .Accepts<RotateProjectCredentialRequest>("application/x-www-form-urlencoded")
                .Produces<ProjectApiKeySecret>().Produces<string>(400).Produces(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(422).ProducesProblem(503);
            // Allowlists: a key's own list and its project's list. Browser forms; ts-auth's 400 reason for a bad entry is shown as is.
            foreach (var target in new[] { "credentials/allowed-cidrs", "credentials/project-allowed-cidrs" })
            {
                var project = target.Contains("project", StringComparison.Ordinal);
                endpoints.MapPost("/api/v1/dashboard/" + target, async (HttpContext http, DashboardSessions sessions, AccountService accounts, ApiKeyService keys) =>
                {
                    var context = await AccountRequest.RequireAsync(http, sessions, accounts);
                    if (context is null)
                    {
                        return Results.Unauthorized();
                    }

                    var form = await http.Request.ReadFormAsync(http.RequestAborted);
                    if (!Csrf.Validate(http, form))
                    {
                        return Results.BadRequest("Invalid form token.");
                    }

                    var cidrs = ApiKeyService.ParseCidrs(form["allowedCidrs"].ToString());
                    try
                    {
                        if (project)
                        {
                            await keys.SetProjectAllowedCidrsAsync(context, cidrs, http.RequestAborted);
                        }
                        else
                        {
                            var key = await keys.SetAllowedCidrsAsync(context, form["keyId"].ToString(), cidrs, http.RequestAborted);
                            if (!DatabaseEndpoints.WantsHtml(http))
                            {
                                return Results.Json(ApiKeyService.CredentialMetadata(key), ApiKeyContractJson.Default.ProjectCredentialMetadata);
                            }
                        }
                    }
                    catch (Exception error) when (DatabaseEndpoints.KnownFailure(error) || error is HttpRequestException)
                    { return DatabaseEndpoints.WantsHtml(http) ? await PageAsync(http, context, keys, DatabaseEndpoints.Message(error), status: DatabaseEndpoints.Status(error)) : DatabaseEndpoints.Failure(error); }
                    return DatabaseEndpoints.WantsHtml(http) ? HttpRedirects.SeeOther("/api-keys" + http.Request.QueryString) : Results.NoContent();
                });
            }
            return endpoints;
        }

        public static async Task<IResult> CreateAsync(HttpContext http, string kind, DashboardSessions sessions, AccountService accounts, ApiKeyService keys)
        {
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null)
            {
                return Results.Unauthorized();
            }

            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            if (!StepUpFlow.IsResuming(http) && !Csrf.Validate(http, form))
            {
                return Results.BadRequest("Invalid form token.");
            }

            try
            {
                if (!context.CanManage)
                {
                    throw new AccountAccessException("Organization management access is required.");
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
                return DatabaseEndpoints.WantsHtml(http) ? await PageAsync(http, context, keys, revealed: credential) : Results.Json(new ProjectApiKeySecret(ApiKeyService.Metadata(credential.Key), credential.Secret), ApiKeyContractJson.Default.ProjectApiKeySecret, statusCode: 201);
            }
            catch (Exception error) when (DatabaseResults.KnownFailure(error) || error is HttpRequestException)
            {
                return DatabaseResults.WantsHtml(http) ? await PageAsync(http, context, keys, DatabaseResults.Message(error), kind, form["name"].ToString(), status: DatabaseResults.Status(error), draftScopes: form["scopes"].ToString(), draftDatabaseId: form["databaseId"].ToString(), draftExpiresInDays: form["expiresInDays"].ToString(), draftAllowedCidrs: form["allowedCidrs"].ToString()) : DatabaseResults.Failure(error);
            }
        }

        public static async Task<IResult> RotateAsync(HttpContext http, DashboardSessions sessions, AccountService accounts, ApiKeyService keys)
        {
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null)
            {
                return Results.Unauthorized();
            }

            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            if (!StepUpFlow.IsResuming(http) && !Csrf.Validate(http, form))
            {
                return Results.BadRequest("Invalid form token.");
            }

            try
            {
                if (!context.CanManage)
                {
                    throw new AccountAccessException("Organization management access is required.");
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
                return DatabaseEndpoints.WantsHtml(http) ? await PageAsync(http, context, keys, revealed: credential) : Results.Json(new ProjectApiKeySecret(ApiKeyService.Metadata(credential.Key), credential.Secret), ApiKeyContractJson.Default.ProjectApiKeySecret);
            }
            catch (Exception error) when (DatabaseEndpoints.KnownFailure(error)) { return DatabaseEndpoints.WantsHtml(http) ? await PageAsync(http, context, keys, DatabaseEndpoints.Message(error), status: DatabaseEndpoints.Status(error)) : DatabaseEndpoints.Failure(error); }
        }

        private static async Task<IResult> PageAsync(HttpContext http, AccountContext context, ApiKeyService service, string error = "", string create = "", string name = "", CreatedProjectCredential? revealed = null, int status = 200, string draftScopes = "", string draftDatabaseId = "", string draftExpiresInDays = "", string draftAllowedCidrs = "")
        {
            var warning = "";
            IReadOnlyList<ProjectCredential> keys = [];
            IReadOnlyList<string>? projectCidrs = null;
            var network = context.Project is null ? Task.FromResult<IReadOnlyList<string>>([]) : service.ProjectAllowedCidrsAsync(context, http.RequestAborted);
            try { keys = await service.ListAsync(context, http.RequestAborted); }
            catch (Exception failure) when (!http.RequestAborted.IsCancellationRequested && (failure is HttpRequestException || revealed is not null && ApiKeyService.FollowUpFailure(failure)))
            {
                if (revealed is null)
                {
                    if (error.Length == 0)
                    {
                        error = "The live credential issuer is unavailable. Try again shortly.";
                    }
                }
                else { keys = [revealed.Key]; warning = (warning + " The key change succeeded, but the complete key list is unavailable. Save this secret now; do not retry the change.").Trim(); }
            }
            try { projectCidrs = await network; }
            catch (Exception failure) when (!http.RequestAborted.IsCancellationRequested && (failure is HttpRequestException or AccountAccessException || revealed is not null && ApiKeyService.FollowUpFailure(failure)))
            {
                if (revealed is not null)
                {
                    warning = (warning + " Network settings could not be refreshed; the key change succeeded.").Trim();
                }
            }
            var now = http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
            try { await ApiKeyExpiryNotifications.AddAsync(http.RequestServices.GetRequiredService<AccountService>(), context, keys, now, http.RequestAborted); }
            catch (Exception failure) when (!http.RequestAborted.IsCancellationRequested && revealed is not null && ApiKeyService.FollowUpFailure(failure))
            { warning = (warning + " Expiry reminders could not be updated; the key change succeeded.").Trim(); }
            var result = ApiKeysView.Create(new(ShellModel.For(http, context, "API keys"), keys, http.Request.Query["q"].ToString(), http.Request.Query["databaseId"].ToString(), error, create.Length > 0 ? create : http.Request.Query["create"].ToString(), name, revealed, http.Request.Query["status"].ToString(), draftScopes, draftDatabaseId, DraftExpiresInDays: draftExpiresInDays, DraftAllowedCidrs: draftAllowedCidrs, ProjectCidrs: projectCidrs, Now: now) { Warning = warning });
            result.StatusCode = status;
            return result;
        }
    }
}
