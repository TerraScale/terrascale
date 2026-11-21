using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using TerraScale.Dashboard.Errors;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.NotFound;
using TerraScale.Dashboard.Common.UI;
using TerraScale.Dashboard.Common.Databases;
using TerraScale.Dashboard.Platform.TsAuth;
using TerraScale.Dashboard.Platform.Datastar;

namespace TerraScale.Dashboard.Features.AuthenticationManagement
{
    // The project's end-user tenant at ts-auth, managed with the signed-in user's own token.
    // ts-auth checks the project role on every call; the dashboard mirrors it: any project role reads, the admin project role changes.
    public static partial class AuthenticationConsole
    {
        public const string Unreachable = "ts-auth is unreachable right now. Try again shortly.";

        internal static async Task<IResult> RootAsync(HttpContext http, DashboardSessions sessions, AccountService accounts, ITsAuthProjects projects, string prefix)
        {
            var section = http.Request.Query["section"].ToString();
            return AuthenticationSection.Find(section) is not null
                ? HttpRedirects.SeeOther(prefix + "/" + Uri.EscapeDataString(section))
                : await PageAsync(http, sessions, accounts, projects, "overview");
        }

        public static async Task<IResult> PostAsync(HttpContext http, string path, DashboardSessions sessions, AccountService accounts, ITsAuthProjects projects)
        {
            var parts = path.Split('/');
            var key = parts[0] switch { "settings" => "sign-in", "organization-picker" => "settings", "providers" => "social", var first => first };
            var known = parts switch
            {
                ["settings" or "organization-picker" or "organizations" or "restrictions" or "invitations" or "branding" or "configuration" or "mfa-sessions" or "attack-protection" or "clients" or "webhooks"] or ["danger", "users" or "sessions" or "clients"] => true,
                ["invitations", _, "revoke"] or ["waitlist", _, "approve" or "reject"] => true,
                ["providers", var provider] => AuthenticationProvider.Find(provider) is not null,
                ["providers", var provider, "delete"] => AuthenticationProvider.Find(provider) is not null,
                ["users", _, "api-keys", _, "revoke"] => true,
                ["agents", "policy"] or ["agents", _, "revoke"] or ["agents", _, "keys", _, "revoke"] => true,
                ["users", _, "disable" or "enable" or "delete" or "impersonate" or "sign-out" or "profile"] or ["users", "impersonations", _, "end"] or ["sessions", _, "revoke"] or ["clients", _, "delete" or "edit" or "rotate"] or ["webhooks", _, "delete" or "rotate" or "test"] or ["webhooks", _, "deliveries", _, "replay"] => true,
                ["organizations", ..] => true,
                _ => false
            };
            if (!known)
            {
                return Results.NotFound();
            }

            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null)
            {
                return Results.Unauthorized();
            }

            var form = await FormRequests.ReadAsync(http, http.RequestAborted);
            if (!StepUpFlow.IsResuming(http) && !Csrf.Validate(http, form))
            {
                return RequestError.FormExpired(http, "/auth/" + key, Results.StatusCode(403));
            }

            if (!(parts is ["organization-picker"] ? context.CanSetOrganizationPicker : context.CanManageProject) || context.Project is null)
            {
                return Results.StatusCode(403);
            }

            var (session, project, cancel) = (context.Session, context.Project.Id, http.RequestAborted);
            try
            {
                if (parts[0] == "organizations")
                {
                    return await OrganizationPostAsync(http, context, projects, form, parts, cancel);
                }

                switch (parts)
                {
                    case ["agents", "policy"]:
                        await projects.UpdateAgentPolicyAsync(session, project, new(Lines(form["allowed_scopes"])), cancel);
                        break;
                    case ["agents", var agent, "revoke"]:
                        await projects.RevokeAgentAsync(session, project, agent, cancel);
                        break;
                    case ["agents", var agent, "keys", var kid, "revoke"]:
                        await projects.RevokeAgentKeyAsync(session, project, agent, kid, cancel);
                        break;
                    case ["invitations"]:
                        await projects.CreateInvitationAsync(session, project, new(form["email"].ToString()), cancel);
                        break;
                    case ["invitations", var invitation, "revoke"]:
                        await projects.RevokeProjectInvitationAsync(session, project, invitation, cancel);
                        break;
                    case ["waitlist", var entry, "approve"]:
                        await projects.ApproveWaitlistAsync(session, project, entry, cancel);
                        break;
                    case ["waitlist", var entry, "reject"]:
                        await projects.RejectWaitlistAsync(session, project, entry, cancel);
                        break;
                    case ["restrictions"]:
                        var current = await projects.GetSettingsAsync(session, project, cancel);
                        await projects.UpdateSettingsAsync(session, project, new(
                            Methods: current.Methods with { SignUpMode = form["sign_up_mode"].ToString() },
                            Restrictions: new(RestrictionList(form["allowlist"]), RestrictionList(form["blocklist"]),
                                form["block_disposable_emails"] == "on", form["block_email_aliases"] == "on")), cancel);
                        break;
                    case ["users", var targetUser, "impersonate"]:
                        var reason = form["reason"].ToString();
                        var clientId = form["client_id"].ToString();
                        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 500 || clientId.Length == 0)
                        {
                            throw new AccountAccessException("Enter a reason of up to 500 characters and choose an application.", 422);
                        }

                        var impersonationIntent = StepUpFlow.Intent(http, context, "start_impersonation");
                        if (impersonationIntent is null)
                        {
                            return await StepUpFlow.StartAsync(http, context, "start_impersonation", "ts-auth", boundOrganizationId: project + ":" + targetUser);
                        }

                        var ticket = await projects.StartImpersonationAsync(session, project, targetUser, new(reason, clientId), impersonationIntent.Challenge.Proof, cancel);
                        return await PageViewAsync(http, context, projects, "users", impersonationTicket: ticket);
                    case ["users", "impersonations", var impersonationId, "end"]:
                        await projects.EndImpersonationAsync(session, project, impersonationId, cancel);
                        break;
                    case ["organization-picker"]:
                        await projects.UpdateSettingsAsync(session, project, new(OrganizationPicker: form.ContainsKey("organization_picker")), cancel);
                        break;
                    case ["settings"]:
                        await projects.UpdateSettingsAsync(session, project, new(Validated(Methods(form["passcode"] == "on", form["password"] == "on", form["sign_up_mode"].ToString(), form["magic_link"] == "on", form["username"] == "on"))), cancel);
                        break;
                    case ["attack-protection"]:
                        await projects.UpdateSettingsAsync(session, project, new(AttackProtection: new(
                            form["bot_challenge"] == "on", form["breached_passwords"] == "on",
                            form["account_lockout"] == "on", form["new_device_email"] == "on")), cancel);
                        break;
                    case ["branding"]:
                        await projects.UpdateSettingsAsync(session, project, new(Branding: Validated(HostedBranding(form))), cancel);
                        break;
                    case ["mfa-sessions"]:
                        await projects.UpdateSettingsAsync(session, project, new(Configuration: Validated(await SecurityConfigurationAsync(projects, session, project, form, cancel))), cancel);
                        break;
                    case ["configuration"]:
                        var currentConfiguration = (await projects.GetSettingsAsync(session, project, cancel)).Configuration ?? new([]);
                        var updatedConfiguration = Validated(Configuration(form["redirect_urls"], form["session_lifetime_seconds"]));
                        await projects.UpdateSettingsAsync(session, project, new(Configuration: currentConfiguration with
                        { RedirectUrls = updatedConfiguration.RedirectUrls, SessionLifetimeSeconds = updatedConfiguration.SessionLifetimeSeconds }), cancel);
                        break;
                    case ["danger", var family]:
                        if (form["confirmation"] != context.Project.Name)
                        {
                            throw new AccountAccessException("Type the project name exactly to confirm.", 422);
                        }

                        var record = form["record_id"].ToString();
                        if (record.Length == 0)
                        {
                            throw new AccountAccessException("Choose a record to remove.", 422);
                        }

                        if (family == "users")
                        {
                            await projects.DeleteUserAsync(session, project, record, cancel);
                        }
                        else if (family == "sessions")
                        {
                            await projects.RevokeSessionAsync(session, project, record, cancel);
                        }
                        else
                        {
                            await projects.DeleteClientAsync(session, project, record, cancel);
                        }

                        break;
                    case ["providers", var provider]:
                        await projects.SetProviderAsync(session, project, provider, Validated(Provider(provider, form["client_id"], form["client_secret"], form["issuer"], form["team_id"], form["key_id"], form["tenant"], provider == "oauth2" ? new TsAuthProjectOAuth2(form["authorize_url"].ToString(), form["token_url"].ToString(), form["userinfo_url"].ToString(), form["scopes"].ToString(), form["id_claim"].ToString(), form["email_claim"].ToString(), form["email_verified_claim"].ToString()) : null)), cancel);
                        break;
                    case ["providers", var provider, _]: await projects.DeleteProviderAsync(session, project, provider, cancel); break;
                    case ["clients"]:
                        var request = Validated(NewClient(form["name"], form["type"], Lines(form["redirect_uris"]), form["allowed_scopes"], form["allowed_audiences"], form["token_lifetime_minutes"]));
                        var intent = StepUpFlow.Intent(http, context, "create_oauth_client");
                        if (intent is null)
                        {
                            return await StepUpFlow.StartAsync(http, context, "create_oauth_client", "ts-auth");
                        }

                        var created = await projects.CreateClientAsync(session, project, request, intent.Challenge.Proof, cancel);
                        return await PageViewAsync(http, context, projects, "clients", created: created);
                    case ["clients", var id, "edit"]:
                        var update = Validated(ClientUpdate(form["name"], Lines(form["redirect_uris"]), form["type"], form["allowed_scopes"], form["allowed_audiences"], form["token_lifetime_minutes"]));
                        var editIntent = StepUpFlow.Intent(http, context, "update_oauth_client");
                        if (editIntent is null)
                        {
                            return await StepUpFlow.StartAsync(http, context, "update_oauth_client", "ts-auth");
                        }

                        await projects.UpdateClientAsync(session, project, id, update, editIntent.Challenge.Proof, cancel);
                        break;
                    case ["clients", var id, "rotate"]:
                        var rotateIntent = StepUpFlow.Intent(http, context, "rotate_oauth_client");
                        if (rotateIntent is null)
                        {
                            return await StepUpFlow.StartAsync(http, context, "rotate_oauth_client", "ts-auth");
                        }

                        var rotated = await projects.RotateClientSecretAsync(session, project, id, rotateIntent.Challenge.Proof, cancel);
                        return await PageViewAsync(http, context, projects, "clients", rotated: rotated);
                    case ["clients", var id, "delete"]: await projects.DeleteClientAsync(session, project, id, cancel); break;
                    case ["webhooks"]:
                        var webhook = Validated(NewWebhook(form["url"], form["event_types"].Select(value => (string?)value)));
                        var createIntent = StepUpFlow.Intent(http, context, "create_webhook_endpoint");
                        if (createIntent is null)
                        {
                            return await StepUpFlow.StartAsync(http, context, "create_webhook_endpoint", "ts-auth");
                        }

                        var registered = await projects.CreateWebhookEndpointAsync(session, project, webhook, createIntent.Challenge.Proof, cancel);
                        return await PageViewAsync(http, context, projects, "webhooks", createdWebhook: registered);
                    case ["webhooks", var id, "rotate"]:
                        var secretIntent = StepUpFlow.Intent(http, context, "rotate_webhook_secret");
                        if (secretIntent is null)
                        {
                            return await StepUpFlow.StartAsync(http, context, "rotate_webhook_secret", "ts-auth");
                        }

                        var rotatedSecret = await projects.RotateWebhookSecretAsync(session, project, id, secretIntent.Challenge.Proof, cancel);
                        return await PageViewAsync(http, context, projects, "webhooks", rotatedWebhook: rotatedSecret);
                    case ["webhooks", var id, "test"]: await projects.TestWebhookAsync(session, project, id, cancel); break;
                    case ["webhooks", var id, "deliveries", var delivery, "replay"]: await projects.ReplayWebhookDeliveryAsync(session, project, id, delivery, cancel); break;
                    case ["webhooks", var id, "delete"]: await projects.DeleteWebhookEndpointAsync(session, project, id, cancel); break;
                    case ["sessions", var id, _]: await projects.RevokeSessionAsync(session, project, id, cancel); break;
                    case ["users", var id, "sign-out"]: await projects.SignOutUserAsync(session, project, id, cancel); break;
                    case ["users", var id, "profile"]:
                        await projects.UpdateUserAsync(session, project, id, new(Value(form["first_name"].ToString()), Value(form["last_name"].ToString()),
                            Value(form["image_url"].ToString()), Value(form["external_id"].ToString()), Metadata(form["public_metadata"]),
                            Metadata(form["private_metadata"]), Metadata(form["unsafe_metadata"])), cancel);
                        return HttpRedirects.SeeOther("/auth/users?user=" + Uri.EscapeDataString(id) + "&saved=1");
                    case ["users", var id, "api-keys", var keyId, "revoke"]:
                        await projects.RevokeUserApiKeyAsync(session, project, id, keyId, cancel);
                        return HttpRedirects.SeeOther("/auth/users?user=" + Uri.EscapeDataString(id) + "&saved=1");
                    case ["users", var id, "delete"]: await projects.DeleteUserAsync(session, project, id, cancel); break;
                    case ["users", var id, var action]: await projects.SetUserDisabledAsync(session, project, id, action == "disable", cancel); break;
                }
            }
            catch (Exception error) when (error is AccountAccessException or HttpRequestException or IOException)
            {
                return await SectionResultAsync(http, context, projects, key, error is AccountAccessException access ? access.Message : Unreachable, error is AccountAccessException { StatusCode: var code } ? code : 503);
            }
            return await SectionResultAsync(http, context, projects, key);
        }

        private static async Task<IResult> SectionResultAsync(HttpContext http, AccountContext context, ITsAuthProjects projects, string key, string error = "", int status = 200)
        {
            if (http.Request.IsDatastarRequest)
            {
                var page = await PageModelAsync(http, context, projects, key, error, status);
                if (key == "agents" && error.Length == 0)
                {
                    page = page with { Saved = true };
                }
                await DatastarEvents.PatchElementsAsync(http, AuthenticationSectionView.Create(page));
                return Results.Empty;
            }

            if (error.Length > 0)
            {
                return await PageViewAsync(http, context, projects, key, error, status);
            }

            var query = key == "agents"
                ? AuthenticationPage.Query(http.Request.Query["agent"].ToString(), http.Request.Query["owner"].ToString(), http.Request.Query["after"].ToString())
                : QueryString.Empty;
            return HttpRedirects.SeeOther("/auth/" + key + query.Add("saved", "1").ToString());
        }

        // At least one first factor stays on; ts-auth enforces the same rule.
        private static string[] RestrictionList(string? value) =>
            (value ?? "").Split(['\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        internal static T Validated<T>(Valid<T> result)
        {
            return result switch
            {
                not null and T value => value,
                Validation error => throw new AccountAccessException(error.Message, 422),
                _ => throw new UnreachableException(),
            };
        }

        public static Valid<TsAuthProjectMethods> Methods(bool passcode, bool password, string signUpMode, bool magicLink = false, bool username = false)
        {
            return passcode || password || magicLink ? new TsAuthProjectMethods(passcode, password, signUpMode, magicLink, username) : new Validation("Keep email passcode, password or magic-link sign-in on.");
        }

        // Empty values clear a branding field.
        public static Valid<TsAuthProjectBranding> Branding(string? name, string? logoUrl, string? themeColor)
        {
            static string? Clean(string? value)
            {
                return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            }

            var (title, logo, color) = (Clean(name), Clean(logoUrl), Clean(themeColor)?.ToLowerInvariant());
            if (title is { Length: > 64 } || title?.Any(char.IsControl) == true)
            {
                return new Validation("Use a name of up to 64 characters.");
            }

            if (logo is not null && !HttpsUrl(logo))
            {
                return new Validation("The logo must be an https URL.");
            }

            if (color is not null && !Color().IsMatch(color))
            {
                return new Validation("The theme color must look like #2158e8.");
            }

            return new TsAuthProjectBranding(title, logo, color);
        }

        // A blank secret keeps the stored one; ts-auth requires it the first time. Never log the update.
        public static Valid<TsAuthProjectProviderUpdate> Provider(string provider, string? clientId, string? clientSecret, string? issuer, string? teamId, string? keyId, string? tenant = null, TsAuthProjectOAuth2? oauth2 = null)
        {
            static string? Clean(string? value)
            {
                return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            }

            var (id, secret, url, team, key) = (Clean(clientId), Clean(clientSecret), Clean(issuer), Clean(teamId)?.ToUpperInvariant(), Clean(keyId)?.ToUpperInvariant());
            if (AuthenticationProvider.Find(provider) is not { } known)
            {
                return new Validation("Choose google, github, apple, microsoft, gitlab, linkedin, slack, discord, facebook, x, oidc or oauth2.");
            }

            if (id is null or { Length: > 256 })
            {
                return new Validation($"Enter the {known.ClientIdLabel.ToLowerInvariant()} of up to 256 characters.");
            }

            if (secret is { Length: > 8192 })
            {
                return new Validation("The client secret is too long.");
            }

            if (provider == "apple" && !(AppleId().IsMatch(team ?? "") && AppleId().IsMatch(key ?? "")))
            {
                return new Validation("The Apple team ID and key ID are 10 letters or digits.");
            }

            if (provider == "oidc" && !(Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.Query.Length == 0 && uri.Fragment.Length == 0))
            {
                return new Validation("The issuer must be an https URL without a query.");
            }

            return provider switch
            {
                "apple" => new TsAuthProjectProviderUpdate(id, secret, TeamId: team, KeyId: key),
                "oidc" => new TsAuthProjectProviderUpdate(id, secret, Issuer: url),
                "microsoft" => new TsAuthProjectProviderUpdate(id, secret, Tenant: Clean(tenant) ?? "common"),
                "oauth2" => new TsAuthProjectProviderUpdate(id, secret, OAuth2: oauth2),
                _ => new TsAuthProjectProviderUpdate(id, secret)
            };
        }

        [GeneratedRegex("^[A-Z0-9]{10}$")]
        private static partial Regex AppleId();

        public static Valid<TsAuthProjectCreateOAuthClient> NewClient(string? name, string? type, IEnumerable<string> redirectUris, string? scopes = null, string? audiences = null, string? minutes = null)
        {
            var uris = redirectUris.Select(uri => uri.Trim()).Where(uri => uri.Length > 0).ToArray();
            if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 64)
            {
                return new Validation("Name the application in up to 64 characters.");
            }

            if (type is not ("public" or "confidential" or "machine"))
            {
                return new Validation("Choose a public, confidential or machine application.");
            }

            if (type == "machine")
            {
                var policy = MachinePolicy(scopes, audiences, minutes);
                return policy is Validation error ? new Valid<TsAuthProjectCreateOAuthClient>(error) : new NewClientPolicy(name.Trim(), type, AsPolicy(policy)).Client();
            }

            if (uris.Length is < 1 or > 10)
            {
                return new Validation("Add 1 to 10 redirect URIs, one per line.");
            }

            return new TsAuthProjectCreateOAuthClient(name.Trim(), type, uris);
        }

        public static readonly string[] LifecycleEvents =
            ["apiKey.created", "apiKey.revoked", "user.created", "user.signed_up", "user.signed_in", "user.disabled", "user.enabled", "user.deleted", "user.provider_linked", "user.provider_unlinked", "user.updated", "session.created", "session.revoked", "webhook.endpoint_disabled", "impersonation.started", "impersonation.ended"];

        public static Valid<TsAuthCreateWebhookEndpoint> NewWebhook(string? url, IEnumerable<string?> eventTypes)
        {
            var trimmed = url?.Trim();
            if (trimmed is not { Length: > 0 }
                || !Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0
                || uri.AbsoluteUri.Length > 2048)
            {
                return new Validation("The URL must be an absolute https URL of up to 2048 characters.");
            }

            var events = eventTypes.Where(value => value is not null).Select(value => value!).Distinct().ToArray();
            if (events.Length == 0)
            {
                return new Validation("Choose at least one lifecycle event.");
            }

            if (events.Any(value => !LifecycleEvents.Contains(value)))
            {
                return new Validation("Choose a valid lifecycle event.");
            }

            return new TsAuthCreateWebhookEndpoint(trimmed, events);
        }

        public static Valid<TsAuthProjectClientUpdate> ClientUpdate(string? name, IEnumerable<string> redirectUris, string? type = null, string? scopes = null, string? audiences = null, string? minutes = null)
        {
            var uris = redirectUris.Select(uri => uri.Trim()).Where(uri => uri.Length > 0).ToArray();
            if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 64)
            {
                return new Validation("Name the application in up to 64 characters.");
            }

            if (type == "machine")
            {
                var policy = MachinePolicy(scopes, audiences, minutes);
                return policy is Validation error ? new Valid<TsAuthProjectClientUpdate>(error) : new NewClientPolicy(name.Trim(), type, AsPolicy(policy)).Update();
            }

            if (uris.Length is < 1 or > 10)
            {
                return new Validation("Add 1 to 10 redirect URIs, one per line.");
            }

            return new TsAuthProjectClientUpdate(name.Trim(), uris);
        }

        private static Valid<MachinePolicyResult> MachinePolicy(string? scopes, string? audiences, string? minutes)
        {
            var allowed = Lines(scopes);
            if (allowed.Length is < 1 or > 64 || !int.TryParse(minutes, out var lifetime) || lifetime is < 5 or > 60)
            {
                return new Validation("Enter 1 to 64 allowed scopes and a token lifetime from 5 to 60 minutes.");
            }

            return new MachinePolicyResult(allowed, Lines(audiences), lifetime);
        }

        private sealed record NewClientPolicy(string Name, string Type, MachinePolicyResult Policy)
        {
            public TsAuthProjectCreateOAuthClient Client()
            {
                return new(Name, Type, [], Policy.Scopes, Policy.Audiences, Policy.Minutes);
            }

            public TsAuthProjectClientUpdate Update()
            {
                return new(Name, [], Policy.Scopes, Policy.Audiences, Policy.Minutes);
            }
        }

        private sealed record MachinePolicyResult(IReadOnlyList<string> Scopes, IReadOnlyList<string> Audiences, int Minutes);

        private static MachinePolicyResult AsPolicy(Valid<MachinePolicyResult> result)
        {
            return result switch
            {
                MachinePolicyResult value => value,
                Validation => throw new UnreachableException(),
            };
        }

        [GeneratedRegex("^#[0-9a-f]{6}$")]
        private static partial Regex Color();

        internal static async Task<IResult> PageAsync(HttpContext http, DashboardSessions sessions, AccountService accounts, ITsAuthProjects projects, string key)
        {
            if (AuthenticationSection.Find(key) is null)
            {
                return await NotFoundEndpoints.PageAsync(http, sessions, accounts);
            }

            var context = await AccountEndpoints.RequireAsync(http, sessions, accounts);
            if (context is null)
            {
                return AccountEndpoints.LoginRedirect(http);
            }

            if (context.Project is null)
            {
                return HttpRedirects.SeeOther("/");
            }

            if (key == "settings" && context.Project.Id != PlatformDatabases.AuthProjectId)
            {
                return await NotFoundEndpoints.PageAsync(http, sessions, accounts);
            }

            return await PageViewAsync(http, context, projects, key);
        }

        private static string? Value(string query)
        {
            return query.Length == 0 ? null : query;
        }

        private static async Task<IResult> PageViewAsync(HttpContext http, AccountContext context, ITsAuthProjects projects, string key, string error = "", int status = 200,
            TsAuthProjectOAuthClient? created = null, TsAuthProjectClientSecret? rotated = null,
            TsAuthWebhookEndpoint? createdWebhook = null, TsAuthWebhookSecret? rotatedWebhook = null, TsAuthImpersonationTicket? impersonationTicket = null)
        {
            return AuthenticationView.Create(await PageModelAsync(http, context, projects, key, error, status, created, rotated, createdWebhook, rotatedWebhook, impersonationTicket));
        }

        private static async Task<AuthenticationPage> PageModelAsync(HttpContext http, AccountContext context, ITsAuthProjects projects, string key, string error = "", int status = 200,
            TsAuthProjectOAuthClient? created = null, TsAuthProjectClientSecret? rotated = null,
            TsAuthWebhookEndpoint? createdWebhook = null, TsAuthWebhookSecret? rotatedWebhook = null, TsAuthImpersonationTicket? impersonationTicket = null)
        {
            var section = AuthenticationSection.Find(key)!;
            if (key == "branding")
            {
                http.Items["auth-branding"] = true;
            }

            var query = http.Request.Query;
            var page = new AuthenticationPage(ShellModel.For(http, context, key == "overview" ? "Authentication" : "Authentication · " + section.Title), section,
                query["q"].ToString(), query["after"].ToString(), query["user"].ToString(), error, created, rotated, createdWebhook, rotatedWebhook)
            { ImpersonationTicket = impersonationTicket, Draft = status >= 400 && http.Request.HasFormContentType ? await http.Request.ReadFormAsync(http.RequestAborted) : null, Saved = query["saved"] == "1" };
            var (session, project, cancel) = (context.Session, context.Project!.Id, http.RequestAborted);
            try
            {
                switch (key)
                {
                    case "danger":
                        page = page with
                        {
                            Users = (await projects.ListUsersAsync(session, project, null, Value(page.After), cancel)).Items,
                            Sessions = (await projects.ListSessionsAsync(session, project, null, Value(page.After), cancel)).Items,
                            Clients = await projects.ListClientsAsync(session, project, cancel)
                        };
                        break;
                    case "users" when page.User.Length > 0:
                        page = page with { SelectedUser = await projects.GetUserAsync(session, project, page.User, cancel), UserApiKeys = (await projects.ListUserApiKeysAsync(session, project, page.User, cancel)).Items };
                        break;
                    case "users":
                        var users = await projects.ListUsersAsync(session, project, Value(page.Search), Value(page.After), cancel);
                        page = page with
                        {
                            Users = users.Items,
                            Next = users.Next,
                            Clients = context.CanManageProject ? await projects.ListClientsAsync(session, project, cancel) : [],
                            Impersonations = context.CanManageProject ? (await projects.ListImpersonationsAsync(session, project, cancel)).Items : []
                        };
                        break;
                    case "agents":
                        page = page with { AgentId = query["agent"].ToString(), AgentOwner = query["owner"].ToString() };
                        if (page.AgentId.Length > 0)
                        {
                            page = page with { SelectedAgent = await projects.GetAgentAsync(session, project, page.AgentId, cancel) };
                        }
                        else
                        {
                            var agents = await projects.ListAgentsAsync(session, project, Value(page.AgentOwner), Value(page.After), cancel);
                            page = page with { Agents = agents.Items, Next = agents.Next, AgentPolicy = await projects.GetAgentPolicyAsync(session, project, cancel) };
                        }
                        break;
                    case "sessions": var live = await projects.ListSessionsAsync(session, project, Value(page.User), Value(page.After), cancel); page = page with { Sessions = live.Items, Next = live.Next }; break;
                    case "invitations": var invitations = await projects.ListInvitationsAsync(session, project, Value(page.After), cancel); page = page with { Invitations = invitations.Items, Next = invitations.Next }; break;
                    case "waitlist": var waitlist = await projects.ListWaitlistAsync(session, project, Value(page.After), cancel); page = page with { Waitlist = waitlist.Items, Next = waitlist.Next }; break;
                    case "organizations": page = await OrganizationsAsync(http, page, projects, cancel); break;
                    case "clients": page = page with { Clients = await projects.ListClientsAsync(session, project, cancel) }; break;
                    case "logs":
                        var events = context.CanManageProject ? await projects.ListEventsAsync(session, project, Value(page.After), cancel) : new TsAuthPlatformPage<TsAuthWebhookEvent>([], null);
                        page = page with { Events = events.Items, Next = events.Next };
                        break;
                    case "webhooks":
                        var hooks = context.CanManageProject ? await projects.ListWebhookEndpointsAsync(session, project, cancel) : [];
                        Dictionary<string, IReadOnlyList<TsAuthWebhookDelivery>> deliveries = [];
                        foreach (var hook in hooks)
                        {
                            deliveries.Add(hook.Id, await projects.ListWebhookDeliveriesAsync(session, project, hook.Id, cancel));
                        }
                        page = page with { Webhooks = hooks, Deliveries = deliveries };
                        break;
                    default: page = page with { Settings = await projects.GetSettingsAsync(session, project, cancel) }; break;
                }
            }
            catch (Exception failure) when (failure is AccountAccessException or HttpRequestException or IOException)
            {
                if (error.Length == 0)
                {
                    page = page with { Error = failure is AccountAccessException ? failure.Message : Unreachable };
                }
            }
            return page;
        }
    }

    public sealed partial record AuthenticationPage(ShellModel Shell, AuthenticationSection Section, string Search, string After, string User, string Error,
        TsAuthProjectOAuthClient? Created, TsAuthProjectClientSecret? Rotated, TsAuthWebhookEndpoint? CreatedWebhook, TsAuthWebhookSecret? RotatedWebhook)
    {
        public AccountContext Context => Shell.Context;
        public TsAuthProjectSettings? Settings { get; init; }
        public TsAuthImpersonationTicket? ImpersonationTicket { get; init; }
        public IReadOnlyList<TsAuthImpersonation> Impersonations { get; init; } = [];
        public IReadOnlyList<TsAuthProjectUser>? Users { get; init; }
        public TsAuthProjectUser? SelectedUser { get; init; }
        public IReadOnlyList<TsAuthUserApiKey>? UserApiKeys { get; init; }
        public IReadOnlyList<TsAuthProjectSession>? Sessions { get; init; }
        public IReadOnlyList<TsAuthProjectOAuthClient>? Clients { get; init; }
        public IReadOnlyList<TsAuthWebhookEndpoint>? Webhooks { get; init; }
        public IReadOnlyList<TsAuthWebhookEvent>? Events { get; init; }
        public IReadOnlyDictionary<string, IReadOnlyList<TsAuthWebhookDelivery>> Deliveries { get; init; } = new Dictionary<string, IReadOnlyList<TsAuthWebhookDelivery>>();
        public IReadOnlyList<TsAuthProjectInvitation>? Invitations { get; init; }
        public IReadOnlyList<TsAuthWaitlistEntry>? Waitlist { get; init; }
        public string? Next { get; init; }
        public IFormCollection? Draft { get; init; }
        public bool Saved { get; init; }
        public string Heading => Section.Key == "overview" ? "Authentication" : "Authentication: " + Section.Title;
    }

    public sealed record AuthenticationSection(string Key, string Title, string Icon, string Group)
    {
        public static AuthenticationSection? Find(string key)
        {
            return All.FirstOrDefault(section => section.Key == key);
        }

        public static readonly AuthenticationSection[] All =
        [
            new("overview", "Overview", "LayoutDashboard", ""),
            new("users", "Users", "Users", "Users"),
            new("agents", "Agents", "Fingerprint", "Users"),
            new("invitations", "Invitations", "Mail", "Users"),
            new("waitlist", "Waitlist", "Users", "Users"),
            new("restrictions", "Restrictions", "Shield", "Security"),
            new("sessions", "Sessions", "Monitor", "Users"),
            new("organizations", "Organizations", "Building2", "Organizations"),
            new("sign-in", "Sign-in methods", "LockKeyhole", "Sign-in"),
            new("social", "Social sign-in", "Fingerprint", "Sign-in"),
            new("configuration", "Configuration", "Settings", "Sign-in"),
            new("settings", "Settings", "Settings", "Sign-in"),
            new("mfa-sessions", "MFA and sessions", "Shield", "Security"),
            new("attack-protection", "Attack protection", "Shield", "Security"),
            new("clients", "OAuth applications", "Boxes", "Developers"),
            new("webhooks", "Webhooks", "Zap", "Developers"),
            new("logs", "Logs", "ScrollText", "Developers"),
            new("branding", "Branding", "Wand2", "Customize"),
            new("danger", "Danger zone", "TriangleAlert", "Danger")
        ];
    }

    // The social sign-in providers ts-auth supports, in its order, with where the customer creates the OAuth client.
    public sealed record AuthenticationProvider(string Key, string Name, string ClientIdLabel, string ConsoleName, string ConsoleUrl)
    {
        public static AuthenticationProvider? Find(string key)
        {
            return All.FirstOrDefault(provider => provider.Key == key);
        }

        public static readonly AuthenticationProvider[] All =
        [
            new("google", "Google", "Client ID", "Google Cloud console credentials", "https://console.cloud.google.com/apis/credentials"),
            new("github", "GitHub", "Client ID", "GitHub Developer settings, OAuth Apps", "https://github.com/settings/developers"),
            new("apple", "Apple", "Services ID", "Apple Developer Certificates, IDs & Profiles", "https://developer.apple.com/account/resources/identifiers/list/serviceId"),
            new("microsoft", "Microsoft", "Client ID", "Microsoft Entra admin center", "https://entra.microsoft.com/"),
            new("gitlab", "GitLab", "Client ID", "GitLab applications", "https://gitlab.com/-/user_settings/applications"),
            new("linkedin", "LinkedIn", "Client ID", "LinkedIn Developers", "https://www.linkedin.com/developers/apps"),
            new("slack", "Slack", "Client ID", "Slack applications", "https://api.slack.com/apps"),
            new("discord", "Discord", "Client ID", "Discord Developer Portal", "https://discord.com/developers/applications"),
            new("facebook", "Facebook", "Client ID", "Meta for Developers", "https://developers.facebook.com/apps/"),
            new("x", "X", "Client ID", "X Developer Portal", "https://developer.x.com/"),
            new("oidc", "OpenID Connect", "Client ID", "", ""),
            new("oauth2", "Custom OAuth2", "Client ID", "", "")
        ];
    }
}
