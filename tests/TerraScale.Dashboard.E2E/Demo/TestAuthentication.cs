using System.Collections.Concurrent;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Platform.TsAuth;

namespace TerraScale.Dashboard.E2E.Demo
{
    // In-memory stand-in for ts-auth's project tenant management API (test hosts and the public demo only).
    // Each project starts with the same synthetic tenant. Pages hold three records so journeys exercise paging.
    sealed class TestAuthentication : ITsAuthProjects
    {
        public IAuthStepUpGateway? StepUp { get; set; }
        private int createdClients;
        public int CreatedClients => Volatile.Read(ref createdClients);
        public const int PageSize = 3;
        private readonly ConcurrentDictionary<string, Tenant> tenants = new(StringComparer.Ordinal);
        private readonly TerraScaleIdGenerator ids = new();

        private sealed class Tenant
        {
            public TsAuthProjectMethods Methods = new(true, true, true);
            public TsAuthProjectConfiguration Configuration = new([], 1209600);
            public TsAuthProjectBranding Branding = new("Northwind", null, "#2158e8");
            public readonly List<TsAuthProjectUser> Users = [];
            public readonly List<TsAuthProjectSession> Sessions = [];
            public readonly List<TsAuthProjectOAuthClient> Clients = [];
            // Configured providers; the stand-in keeps no secrets, like ts-auth never returns them.
            public readonly Dictionary<string, TsAuthProjectProviderUpdate> Providers = new(StringComparer.Ordinal);
        }

        private Tenant For(string projectId)
        {
            return tenants.GetOrAdd(projectId, _ =>
        {
            var tenant = new Tenant();
            var now = DateTimeOffset.UtcNow;
            foreach (var (email, days) in new[] { ("ada@northwind.app", 10), ("grace@acme.com", 8), ("alan@globex.io", 6), ("kj@initech.dev", 4), ("linus@umbrella.health", 2) })
            {
                tenant.Users.Add(new(ids.NewId(), email, now.AddDays(-days), false));
            }

            foreach (var user in tenant.Users.Take(2))
            {
                tenant.Sessions.Add(new(ids.NewId(), user.Id, user.Email, now.AddHours(-1), now.AddDays(7)));
            }

            tenant.Clients.Add(new(ids.NewId(), "Northwind web", "public", ["https://northwind.app/callback"], now.AddDays(-9)));
            return tenant;
        });
        }

        private static T Locked<T>(Tenant tenant, Func<T> action)
        {
            lock (tenant)
            {
                return action();
            }
        }
        private static AccountAccessException Missing()
        {
            return new("Authentication for this project, or the requested record, is unavailable.", 404);
        }

        private static TsAuthPlatformPage<T> Page<T>(IEnumerable<T> items, Func<T, string> id, string? after)
        {
            var rest = items.SkipWhile(item => after is not null && id(item) != after).Skip(after is null ? 0 : 1).Take(PageSize + 1).ToArray();
            return new(rest.Take(PageSize).ToArray(), rest.Length > PageSize ? id(rest[PageSize - 1]) : null);
        }

        public Task<TsAuthProjectSettings> GetSettingsAsync(SessionRecord session, string projectId, CancellationToken cancellationToken)
        {
            var tenant = For(projectId);
            return Task.FromResult(Locked(tenant, () => new TsAuthProjectSettings(projectId, "https://auth.terrascale.tech/" + projectId,
                "https://auth.terrascale.tech/" + projectId + "/.well-known/openid-configuration", "https://auth.terrascale.tech/ui/" + projectId + "/sign-in",
                tenant.Methods, tenant.Branding, new(tenant.Users.Count, tenant.Clients.Count),
                new[] { "google", "github", "apple", "oidc" }.Select(key => tenant.Providers.TryGetValue(key, out var set)
                    ? new TsAuthProjectProvider(key, true, set.ClientId, set.Issuer, set.TeamId, set.KeyId, Callback(projectId, key))
                    : new TsAuthProjectProvider(key, false, null, null, null, null, Callback(projectId, key))).ToArray(), tenant.Configuration)));
        }

        private static string Callback(string projectId, string provider)
        {
            return $"https://auth.terrascale.tech/ui/{projectId}/social/{provider}/callback";
        }

        public Task<TsAuthProjectSettings> SetProviderAsync(SessionRecord session, string projectId, string provider, TsAuthProjectProviderUpdate update, CancellationToken cancellationToken)
        {
            var tenant = For(projectId);
            Locked(tenant, () =>
            {
                if (update.ClientSecret is null && !tenant.Providers.ContainsKey(provider))
                {
                    throw new AccountAccessException("ts-auth rejected these values. Check them and try again.", 422, "invalid_request");
                }

                return tenant.Providers[provider] = update with { ClientSecret = null };
            });
            return GetSettingsAsync(session, projectId, cancellationToken);
        }

        public Task<TsAuthProjectSettings> DeleteProviderAsync(SessionRecord session, string projectId, string provider, CancellationToken cancellationToken)
        {
            var tenant = For(projectId);
            Locked(tenant, () => tenant.Providers.Remove(provider));
            return GetSettingsAsync(session, projectId, cancellationToken);
        }

        public Task<TsAuthProjectSettings> UpdateSettingsAsync(SessionRecord session, string projectId, TsAuthProjectSettingsUpdate update, CancellationToken cancellationToken)
        {
            var tenant = For(projectId);
            Locked(tenant, () =>
            {
                if (update.Methods is { Passcode: false, Password: false })
                {
                    throw new AccountAccessException("ts-auth rejected these values. Check them and try again.", 422, "invalid_request");
                }

                tenant.Methods = update.Methods ?? tenant.Methods;
                tenant.Branding = update.Branding ?? tenant.Branding;
                tenant.Configuration = update.Configuration ?? tenant.Configuration;
                return true;
            });
            return GetSettingsAsync(session, projectId, cancellationToken);
        }

        public Task<TsAuthPlatformPage<TsAuthProjectUser>> ListUsersAsync(SessionRecord session, string projectId, string? query, string? after, CancellationToken cancellationToken)
        {
            var tenant = For(projectId);
            return Task.FromResult(Locked(tenant, () => Page(tenant.Users.Where(user => string.IsNullOrWhiteSpace(query) || user.Email.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase)), user => user.Id, after)));
        }

        public Task<TsAuthProjectUser> SetUserDisabledAsync(SessionRecord session, string projectId, string userId, bool disabled, CancellationToken cancellationToken)
        {
            var tenant = For(projectId);
            return Task.FromResult(Locked(tenant, () =>
            {
                var index = tenant.Users.FindIndex(user => user.Id == userId);
                if (index < 0)
                {
                    throw Missing();
                }

                if (disabled)
                {
                    tenant.Sessions.RemoveAll(item => item.UserId == userId);
                }

                return tenant.Users[index] = tenant.Users[index] with { Disabled = disabled };
            }));
        }

        public Task<TsAuthUserExportPage> ExportUsersAsync(SessionRecord session, string projectId, string? after, CancellationToken cancellationToken)
        {
            throw new AccountAccessException("Directory exports require the project's ts-auth service.", 503);
        }

        public Task DeleteUserAsync(SessionRecord session, string projectId, string userId, CancellationToken cancellationToken)
        {
            var tenant = For(projectId);
            Locked(tenant, () => tenant.Users.RemoveAll(user => user.Id == userId) == 0 ? throw Missing() : tenant.Sessions.RemoveAll(item => item.UserId == userId));
            return Task.CompletedTask;
        }

        public Task<TsAuthPlatformPage<TsAuthProjectSession>> ListSessionsAsync(SessionRecord session, string projectId, string? userId, string? after, CancellationToken cancellationToken)
        {
            var tenant = For(projectId);
            return Task.FromResult(Locked(tenant, () => Page(tenant.Sessions.Where(item => string.IsNullOrWhiteSpace(userId) || item.UserId == userId), item => item.Id, after)));
        }

        public Task RevokeSessionAsync(SessionRecord session, string projectId, string sessionId, CancellationToken cancellationToken)
        {
            var tenant = For(projectId);
            Locked(tenant, () => tenant.Sessions.RemoveAll(item => item.Id == sessionId) == 0 ? throw Missing() : true);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TsAuthProjectOAuthClient>> ListClientsAsync(SessionRecord session, string projectId, CancellationToken cancellationToken)
        {
            var tenant = For(projectId);
            return Task.FromResult(Locked(tenant, () => (IReadOnlyList<TsAuthProjectOAuthClient>)tenant.Clients.ToArray()));
        }

        public async Task<TsAuthProjectOAuthClient> CreateClientAsync(SessionRecord session, string projectId, TsAuthProjectCreateOAuthClient request, string proof, CancellationToken cancellationToken)
        {
            await (StepUp ?? throw StepUpFlow.Required()).ConsumeAsync(session, proof, "create_oauth_client", "ts-auth", null, cancellationToken);
            Interlocked.Increment(ref createdClients);
            var tenant = For(projectId);
            var client = new TsAuthProjectOAuthClient(ids.NewId(), request.Name!, request.Type!, request.RedirectUris!.ToArray(), DateTimeOffset.UtcNow);
            Locked(tenant, () => { tenant.Clients.Add(client); return true; });
            return request.Type == "confidential" ? client with { ClientSecret = "tsas_test_" + Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)) } : client;
        }

        public Task DeleteClientAsync(SessionRecord session, string projectId, string clientId, CancellationToken cancellationToken)
        {
            var tenant = For(projectId);
            Locked(tenant, () => tenant.Clients.RemoveAll(item => item.ClientId == clientId) == 0 ? throw Missing() : true);
            return Task.CompletedTask;
        }
    }
}
