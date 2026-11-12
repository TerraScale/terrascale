using System.Text.RegularExpressions;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.NotFound;
using TerraScale.Dashboard.Common.UI;
using TerraScale.Dashboard.Platform.TsAuth;

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
            var key = parts[0] switch { "settings" => "sign-in", "providers" => "social", var first => first };
            var known = parts switch
            {
                ["settings" or "branding" or "configuration" or "clients" or "webhooks"] or ["danger", "users" or "sessions" or "clients"] => true,
                ["providers", var provider] => AuthenticationProvider.Find(provider) is not null,
                ["providers", var provider, "delete"] => AuthenticationProvider.Find(provider) is not null,
                ["users", _, "api-keys", _, "revoke"] or ["users", _, "disable" or "enable" or "delete"] or ["sessions", _, "revoke"] or ["clients", _, "delete" or "edit" or "rotate"] or ["webhooks", _, "delete" or "rotate" or "test"] or ["webhooks", _, "deliveries", _, "replay"] => true,
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

            if (!context.CanManageProject || context.Project is null)
            {
                return Results.StatusCode(403);
            }

            var (session, project, cancel) = (context.Session, context.Project.Id, http.RequestAborted);
            try
            {
                switch (parts)
                {
                    case ["settings"]:
                        await projects.UpdateSettingsAsync(session, project, new(Methods(form["passcode"] == "on", form["password"] == "on", form["sign_up"] == "on")), cancel);
                        break;
                    case ["branding"]:
                        await projects.UpdateSettingsAsync(session, project, new(Branding: HostedBranding(form)), cancel);
                        break;
                    case ["configuration"]:
                        await projects.UpdateSettingsAsync(session, project, new(Configuration: Configuration(form["redirect_urls"], form["session_lifetime_seconds"])), cancel);
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
                        await projects.SetProviderAsync(session, project, provider, Provider(provider, form["client_id"], form["client_secret"], form["issuer"], form["team_id"], form["key_id"]), cancel);
                        break;
                    case ["providers", var provider, _]: await projects.DeleteProviderAsync(session, project, provider, cancel); break;
                    case ["clients"]:
                        var request = NewClient(form["name"], form["type"], Lines(form["redirect_uris"]), form["allowed_scopes"], form["allowed_audiences"], form["token_lifetime_minutes"]);
                        var intent = StepUpFlow.Intent(http, context, "create_oauth_client");
                        if (intent is null)
                        {
                            return await StepUpFlow.StartAsync(http, context, "create_oauth_client", "ts-auth");
                        }

                        var created = await projects.CreateClientAsync(session, project, request, intent.Challenge.Proof, cancel);
                        return await PageAsync(http, context, projects, "clients", created: created);
                    case ["clients", var id, "edit"]:
                        var update = ClientUpdate(form["name"], Lines(form["redirect_uris"]), form["type"], form["allowed_scopes"], form["allowed_audiences"], form["token_lifetime_minutes"]);
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
                        return await PageAsync(http, context, projects, "clients", rotated: rotated);
                    case ["clients", var id, "delete"]: await projects.DeleteClientAsync(session, project, id, cancel); break;
                    case ["webhooks"]:
                        var webhook = NewWebhook(form["url"], form["event_types"].Select(value => (string?)value));
                        var createIntent = StepUpFlow.Intent(http, context, "create_webhook_endpoint");
                        if (createIntent is null)
                        {
                            return await StepUpFlow.StartAsync(http, context, "create_webhook_endpoint", "ts-auth");
                        }

                        var registered = await projects.CreateWebhookEndpointAsync(session, project, webhook, createIntent.Challenge.Proof, cancel);
                        return await PageAsync(http, context, projects, "webhooks", createdWebhook: registered);
                    case ["webhooks", var id, "rotate"]:
                        var secretIntent = StepUpFlow.Intent(http, context, "rotate_webhook_secret");
                        if (secretIntent is null)
                        {
                            return await StepUpFlow.StartAsync(http, context, "rotate_webhook_secret", "ts-auth");
                        }

                        var rotatedSecret = await projects.RotateWebhookSecretAsync(session, project, id, secretIntent.Challenge.Proof, cancel);
                        return await PageAsync(http, context, projects, "webhooks", rotatedWebhook: rotatedSecret);
                    case ["webhooks", var id, "test"]: await projects.TestWebhookAsync(session, project, id, cancel); break;
                    case ["webhooks", var id, "deliveries", var delivery, "replay"]: await projects.ReplayWebhookDeliveryAsync(session, project, id, delivery, cancel); break;
                    case ["webhooks", var id, "delete"]: await projects.DeleteWebhookEndpointAsync(session, project, id, cancel); break;
                    case ["sessions", var id, _]: await projects.RevokeSessionAsync(session, project, id, cancel); break;
                    case ["users", var id, "api-keys", var keyId, "revoke"]:
                        await projects.RevokeUserApiKeyAsync(session, project, id, keyId, cancel);
                        return HttpRedirects.SeeOther("/auth/users?user=" + Uri.EscapeDataString(id) + "&saved=1");
                    case ["users", var id, "delete"]: await projects.DeleteUserAsync(session, project, id, cancel); break;
                    case ["users", var id, var action]: await projects.SetUserDisabledAsync(session, project, id, action == "disable", cancel); break;
                }
            }
            catch (Exception error) when (error is AccountAccessException or HttpRequestException or IOException)
            {
                return await PageAsync(http, context, projects, key, error is AccountAccessException access ? access.Message : Unreachable, error is AccountAccessException { StatusCode: var code } ? code : 503);
            }
            return HttpRedirects.SeeOther("/auth/" + key + "?saved=1");
        }

        // At least one first factor stays on; ts-auth enforces the same rule.
        public static TsAuthProjectMethods Methods(bool passcode, bool password, bool signUp)
        {
            return passcode || password ? new(passcode, password, signUp) : throw new AccountAccessException("Keep email passcode or password sign-in on.", 422);
        }

        // Empty values clear a branding field.
        public static TsAuthProjectBranding Branding(string? name, string? logoUrl, string? themeColor)
        {
            static string? Clean(string? value)
            {
                return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            }

            var (title, logo, color) = (Clean(name), Clean(logoUrl), Clean(themeColor)?.ToLowerInvariant());
            if (title is { Length: > 64 } || title?.Any(char.IsControl) == true)
            {
                throw new AccountAccessException("Use a name of up to 64 characters.", 422);
            }

            if (logo is not null && !HttpsUrl(logo))
            {
                throw new AccountAccessException("The logo must be an https URL.", 422);
            }

            if (color is not null && !Color().IsMatch(color))
            {
                throw new AccountAccessException("The theme color must look like #2158e8.", 422);
            }

            return new(title, logo, color);
        }

        // A blank secret keeps the stored one; ts-auth requires it the first time. Never log the update.
        public static TsAuthProjectProviderUpdate Provider(string provider, string? clientId, string? clientSecret, string? issuer, string? teamId, string? keyId)
        {
            static string? Clean(string? value)
            {
                return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            }

            var (id, secret, url, team, key) = (Clean(clientId), Clean(clientSecret), Clean(issuer), Clean(teamId)?.ToUpperInvariant(), Clean(keyId)?.ToUpperInvariant());
            if (AuthenticationProvider.Find(provider) is not { } known)
            {
                throw new AccountAccessException("Choose google, github, apple or oidc.", 422);
            }

            if (id is null or { Length: > 256 })
            {
                throw new AccountAccessException($"Enter the {known.ClientIdLabel.ToLowerInvariant()} of up to 256 characters.", 422);
            }

            if (secret is { Length: > 8192 })
            {
                throw new AccountAccessException("The client secret is too long.", 422);
            }

            if (provider == "apple" && !(AppleId().IsMatch(team ?? "") && AppleId().IsMatch(key ?? "")))
            {
                throw new AccountAccessException("The Apple team ID and key ID are 10 letters or digits.", 422);
            }

            if (provider == "oidc" && !(Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.Query.Length == 0 && uri.Fragment.Length == 0))
            {
                throw new AccountAccessException("The issuer must be an https URL without a query.", 422);
            }

            return provider switch
            {
                "apple" => new(id, secret, TeamId: team, KeyId: key),
                "oidc" => new(id, secret, Issuer: url),
                _ => new(id, secret)
            };
        }

        [GeneratedRegex("^[A-Z0-9]{10}$")]
        private static partial Regex AppleId();

        public static TsAuthProjectCreateOAuthClient NewClient(string? name, string? type, IEnumerable<string> redirectUris, string? scopes = null, string? audiences = null, string? minutes = null)
        {
            var uris = redirectUris.Select(uri => uri.Trim()).Where(uri => uri.Length > 0).ToArray();
            if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 64)
            {
                throw new AccountAccessException("Name the application in up to 64 characters.", 422);
            }

            if (type is not ("public" or "confidential" or "machine"))
            {
                throw new AccountAccessException("Choose a public, confidential or machine application.", 422);
            }

            if (type == "machine")
            {
                var policy = MachinePolicy(scopes, audiences, minutes);
                return new(name.Trim(), type, [], policy.Scopes, policy.Audiences, policy.Minutes);
            }

            if (uris.Length is < 1 or > 10)
            {
                throw new AccountAccessException("Add 1 to 10 redirect URIs, one per line.", 422);
            }

            return new(name.Trim(), type, uris);
        }

        public static readonly string[] LifecycleEvents =
            ["apiKey.created", "apiKey.revoked", "user.signed_up", "user.signed_in", "user.disabled", "user.enabled", "user.deleted", "user.provider_linked", "user.provider_unlinked", "user.updated", "session.created", "session.revoked", "webhook.endpoint_disabled"];

        public static TsAuthCreateWebhookEndpoint NewWebhook(string? url, IEnumerable<string?> eventTypes)
        {
            var trimmed = url?.Trim();
            if (trimmed is not { Length: > 0 }
                || !Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0
                || uri.AbsoluteUri.Length > 2048)
            {
                throw new AccountAccessException("The URL must be an absolute https URL of up to 2048 characters.", 422);
            }

            var events = eventTypes.Where(value => value is not null).Select(value => value!).Distinct().ToArray();
            if (events.Length == 0)
            {
                throw new AccountAccessException("Choose at least one lifecycle event.", 422);
            }

            if (events.Any(value => !LifecycleEvents.Contains(value)))
            {
                throw new AccountAccessException("Choose a valid lifecycle event.", 422);
            }

            return new(trimmed, events);
        }

        public static TsAuthProjectClientUpdate ClientUpdate(string? name, IEnumerable<string> redirectUris, string? type = null, string? scopes = null, string? audiences = null, string? minutes = null)
        {
            var uris = redirectUris.Select(uri => uri.Trim()).Where(uri => uri.Length > 0).ToArray();
            if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 64)
            {
                throw new AccountAccessException("Name the application in up to 64 characters.", 422);
            }

            if (type == "machine")
            {
                var policy = MachinePolicy(scopes, audiences, minutes);
                return new(name.Trim(), [], policy.Scopes, policy.Audiences, policy.Minutes);
            }

            if (uris.Length is < 1 or > 10)
            {
                throw new AccountAccessException("Add 1 to 10 redirect URIs, one per line.", 422);
            }

            return new(name.Trim(), uris);
        }

        private static (IReadOnlyList<string> Scopes, IReadOnlyList<string> Audiences, int Minutes) MachinePolicy(string? scopes, string? audiences, string? minutes)
        {
            var allowed = Lines(scopes);
            if (allowed.Length is < 1 or > 64 || !int.TryParse(minutes, out var lifetime) || lifetime is < 5 or > 60)
            {
                throw new AccountAccessException("Enter 1 to 64 allowed scopes and a token lifetime from 5 to 60 minutes.", 422);
            }

            return (allowed, Lines(audiences), lifetime);
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

            return await PageAsync(http, context, projects, key);
        }

        private static string? Value(string query)
        {
            return query.Length == 0 ? null : query;
        }

        private static async Task<IResult> PageAsync(HttpContext http, AccountContext context, ITsAuthProjects projects, string key, string error = "", int status = 200,
            TsAuthProjectOAuthClient? created = null, TsAuthProjectClientSecret? rotated = null,
            TsAuthWebhookEndpoint? createdWebhook = null, TsAuthWebhookSecret? rotatedWebhook = null)
        {
            var section = AuthenticationSection.Find(key)!;
            if (key == "branding")
            {
                http.Items["auth-branding"] = true;
            }

            var query = http.Request.Query;
            var page = new AuthenticationPage(ShellModel.For(http, context, key == "overview" ? "Authentication" : "Authentication · " + section.Title), section,
                query["q"].ToString(), query["after"].ToString(), query["user"].ToString(), error, created, rotated, createdWebhook, rotatedWebhook)
            { Draft = status >= 400 && http.Request.HasFormContentType ? await http.Request.ReadFormAsync(http.RequestAborted) : null, Saved = query["saved"] == "1" };
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
                    case "users":
                        var users = await projects.ListUsersAsync(session, project, Value(page.Search), Value(page.After), cancel);
                        page = page with { Users = users.Items, Next = users.Next };
                        if (page.User.Length > 0)
                        {
                            page = page with { UserApiKeys = (await projects.ListUserApiKeysAsync(session, project, page.User, cancel)).Items };
                        }
                        break;
                    case "sessions": var live = await projects.ListSessionsAsync(session, project, Value(page.User), Value(page.After), cancel); page = page with { Sessions = live.Items, Next = live.Next }; break;
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
            var result = AuthenticationView.Create(page);
            result.StatusCode = status;
            return result;
        }
    }

    public sealed record AuthenticationPage(ShellModel Shell, AuthenticationSection Section, string Search, string After, string User, string Error,
        TsAuthProjectOAuthClient? Created, TsAuthProjectClientSecret? Rotated, TsAuthWebhookEndpoint? CreatedWebhook, TsAuthWebhookSecret? RotatedWebhook)
    {
        public AccountContext Context => Shell.Context;
        public TsAuthProjectSettings? Settings { get; init; }
        public IReadOnlyList<TsAuthProjectUser>? Users { get; init; }
        public IReadOnlyList<TsAuthUserApiKey>? UserApiKeys { get; init; }
        public IReadOnlyList<TsAuthProjectSession>? Sessions { get; init; }
        public IReadOnlyList<TsAuthProjectOAuthClient>? Clients { get; init; }
        public IReadOnlyList<TsAuthWebhookEndpoint>? Webhooks { get; init; }
        public IReadOnlyList<TsAuthWebhookEvent>? Events { get; init; }
        public IReadOnlyDictionary<string, IReadOnlyList<TsAuthWebhookDelivery>> Deliveries { get; init; } = new Dictionary<string, IReadOnlyList<TsAuthWebhookDelivery>>();
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
            new("sessions", "Sessions", "Monitor", "Users"),
            new("sign-in", "Sign-in methods", "LockKeyhole", "Sign-in"),
            new("social", "Social sign-in", "Fingerprint", "Sign-in"),
            new("configuration", "Configuration", "Settings", "Sign-in"),
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
            new("oidc", "OpenID Connect", "Client ID", "", "")
        ];
    }
}
