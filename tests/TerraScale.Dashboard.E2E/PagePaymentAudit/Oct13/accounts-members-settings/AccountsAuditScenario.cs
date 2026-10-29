using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Organizations;
using TerraScale.Dashboard.Features.Settings;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet.TerraBase;
using TUnit.Core.Interfaces;

namespace TerraScale.Dashboard.E2E.PagePaymentAudit.Oct13.AccountsMembersSettings
{
    // Synthetic records only. Native sign-in, authorization, rendering and mutations
    // still run through the application; existing shared fixture files stay unchanged.
    public sealed class AccountsAuditScenario : IAsyncInitializer, IAsyncDisposable
    {
        public const string OrganizationId = "01J00000000000000000000100";
        public const string ProjectId = "01J00000000000000000000101";
        public const string OwnerId = "01J00000000000000000000001";
        public const string MemberId = "01J00000000000000000000002";
        public const string AdminId = "01J00000000000000000000003";
        public const string Password = "local-audit-password";
        private WebApplication? app;
        private TestWorkspace? workspace;
        private AuditAccess? access;
        public Uri Origin { get; private set; } = null!;
        public string Root { get; } = FindRoot();
        public AuditSettings Settings { get; } = new();
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task InitializeAsync()
        {
            var environment = new DemoEnvironment();
            await environment.InitializeAsync();
            workspace = environment.Workspace;
            var org = new OrganizationRecord
            {
                Id = OrganizationId,
                Name = "Audit Organization",
                Kind = "business",
                Slug = "audit-organization",
                OwnerUserId = OwnerId,
                PlanId = "7CNW2H87JE68ATZX224CV2G625",
                BillingStatus = "active"
            };
            org.Projects[ProjectId] = new()
            {
                Id = ProjectId,
                OrganizationId = OrganizationId,
                Name = "Audit Project",
                Slug = "audit-project",
                Region = "GRU",
                Regions = ["GRU"],
                IsDefault = true
            };
            var principals = new[]
            {
                new ReferencePrincipal(OwnerId, "Audit Owner", "owner@accounts-audit.example.test", Password),
                new ReferencePrincipal(MemberId, "Audit Member", "member@accounts-audit.example.test", Password),
                new ReferencePrincipal(AdminId, "Audit Admin", "admin@accounts-audit.example.test", Password)
            };
            foreach (var principal in principals)
            {
                org.Members[principal.Subject] = new()
                {
                    UserId = principal.Subject,
                    Name = principal.Name,
                    Email = principal.Email,
                    Role = principal.Subject == OwnerId ? "owner" : principal.Subject == AdminId ? "admin" : "member"
                };
            }

            for (var index = 0; index < 10; index++)
            {
                var id = "01J" + new string('0', 20) + (200 + index).ToString("D3");
                org.Members[id] = new() { UserId = id, Name = "Page Member " + index.ToString("D2"), Email = "page-member-" + index.ToString("D2") + "@example.test", Role = "member" };
            }
            for (var index = 0; index < 12; index++)
            {
                var id = "01J" + new string('0', 20) + (300 + index).ToString("D3");
                org.Invitations[id] = new()
                {
                    Id = id,
                    OrganizationId = OrganizationId,
                    Email = "invite-" + index.ToString("D2") + "@accounts-audit.example.test",
                    Role = "member",
                    Status = "revoked",
                    CreatedAt = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                    ExpiresAt = new(2026, 1, 8, 0, 0, 0, TimeSpan.Zero)
                };
            }
            lock (workspace.Gate)
            {
                workspace.Organizations.Add(OrganizationId, TestWorkspace.Clone(org));
            }

            workspace.SeedInvitationMetadata(org);
            IDashboardState state = null!;
            app = environment.CreateTestHost(Root, 0, "http://127.0.0.1", builder =>
            {
                state = (IDashboardState)builder.Services.Last(item => item.ServiceType == typeof(IDashboardState)).ImplementationInstance!;
                var auth = (TestAuth)builder.Services.Last(item => item.ServiceType == typeof(IAuthGateway)).ImplementationInstance!;
                foreach (var principal in principals)
                {
                    auth.RegisterFixturePrincipal(principal);
                }

                IOrganizationAccess original = new TsOrganizationAccess(workspace);
                access = new(original);
                builder.Services.Replace(ServiceDescriptor.Singleton<IOrganizationAccess>(access));
                builder.Services.Replace(ServiceDescriptor.Singleton<ISettingsBackend>(Settings));
            });
            await state.PutAsync(AccountService.OrganizationsCollection, org.Id, JsonSerializer.SerializeToElement(org), TerraBasePrecondition.Absent, default);
            foreach (var principal in principals)
            {
                var account = new AccountRecord
                {
                    Id = AccountService.AccountId(principal.Subject),
                    UserId = principal.Subject,
                    Name = principal.Name,
                    Email = principal.Email,
                    OnboardingCompleted = true,
                    OnboardingStep = "",
                    OrganizationIds = [OrganizationId],
                    ActiveOrganizationId = OrganizationId,
                    ActiveProjectId = ProjectId
                };
                await state.PutAsync(AccountService.AccountsCollection, account.Id, JsonSerializer.SerializeToElement(account), TerraBasePrecondition.Absent, default);
            }
            // Serve exact committed sources without generating or rewriting web/static.
            app.MapGet("/js/datastar.js", () => Results.File(Path.Combine(Root, "src/TerraScale.Dashboard/web/assets/js/vendor/datastar.js"), "text/javascript"));
            app.MapGet("/js/button-loading.js", () => Results.File(Path.Combine(Root, "src/TerraScale.Dashboard/web/assets/js/button-loading.js"), "text/javascript"));
            app.MapGet("/js/workspace.js", () => Results.File(Path.Combine(Root, "src/TerraScale.Dashboard/web/assets/js/workspace.js"), "text/javascript"));
            app.MapGet("/__audit/access-unavailable", () => { access!.FailRead = true; return Results.Ok(); });
            app.MapGet("/__audit/access-recovered", () => { access!.FailRead = false; return Results.Ok(); });
            app.MapGet("/__audit/finish", () => { Finished.TrySetResult(); return Results.Ok(); });
            await app.StartAsync();
            Origin = new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()
                .Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal));
        }

        public HttpClient CreateClient()
        {
            return new(new HttpClientHandler { ServerCertificateCustomValidationCallback = TestHttps.Validate, AllowAutoRedirect = false, CookieContainer = new() })
            { BaseAddress = Origin, Timeout = TimeSpan.FromSeconds(15) };
        }

        public async Task<HttpClient> LoginAsync(string persona = "owner")
        {
            var client = CreateClient();
            using var login = await HttpJourney.LoginAsync(client, persona + "@accounts-audit.example.test", Password, "/organization?tab=members");
            if (login.StatusCode != HttpStatusCode.SeeOther)
            {
                client.Dispose();
                throw new InvalidOperationException("Local audit sign-in failed: " + login.StatusCode);
            }
            return client;
        }

        public async ValueTask DisposeAsync()
        {
            if (app is not null) { await app.StopAsync(); await app.DisposeAsync(); app = null; }
            if (workspace is not null) { lock (workspace.Gate)
                {
                    workspace.Organizations.Remove(OrganizationId);
                }
            }
        }

        private static string FindRoot()
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "TerraScale.slnx")))
                {
                    return directory.FullName;
                }
            }

            throw new DirectoryNotFoundException("Run audit binaries inside this checkout.");
        }

        private sealed class AuditAccess(IOrganizationAccess original) : IOrganizationAccess
        {
            public bool FailRead { get; set; }
            public Task<OrganizationAccessView> ReadAsync(AccountContext context, CancellationToken cancellationToken)
            {
                return FailRead
                ? Task.FromException<OrganizationAccessView>(new IOException("Synthetic private provider detail"))
                : original.ReadAsync(context, cancellationToken);
            }

            public Task<CreatedInvitation> InviteAsync(AccountContext context, string email, string role, string? projectId, CancellationToken cancellationToken)
            {
                return original.InviteAsync(context, email, role, projectId, cancellationToken);
            }

            public Task RevokeInvitationAsync(AccountContext context, string id, CancellationToken cancellationToken)
            {
                return original.RevokeInvitationAsync(context, id, cancellationToken);
            }

            public Task ChangeRoleAsync(AccountContext context, string userId, string role, CancellationToken cancellationToken)
            {
                return original.ChangeRoleAsync(context, userId, role, cancellationToken);
            }

            public Task RemoveMemberAsync(AccountContext context, string userId, CancellationToken cancellationToken)
            {
                return original.RemoveMemberAsync(context, userId, cancellationToken);
            }

            public Task TransferAsync(AccountContext context, string operation, string? userId, CancellationToken cancellationToken)
            {
                return original.TransferAsync(context, operation, userId, cancellationToken);
            }
        }

        public sealed class AuditSettings : ISettingsBackend
        {
            public int WriteAttempts { get; private set; }
            public Task<int> AuditRetentionAsync(AccountContext context, CancellationToken cancellationToken)
            {
                return Task.FromResult(30);
            }

            public Task<int> SetAuditRetentionAsync(AccountContext context, int days, CancellationToken cancellationToken)
            {
                WriteAttempts++;
                return Task.FromException<int>(new IOException("Synthetic private settings detail"));
            }
        }
    }
}
