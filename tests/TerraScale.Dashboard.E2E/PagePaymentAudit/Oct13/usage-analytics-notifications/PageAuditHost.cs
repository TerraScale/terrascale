using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Auth;

namespace TerraScale.Dashboard.E2E.PagePaymentAudit.Oct13;

// Fresh synthetic records only; no captured reference persona or production services.
public sealed class PageAuditHost : IAsyncDisposable
{
    private readonly DemoEnvironment environment = new();
    private readonly AuditClock clock = new();
    private WebApplication? first;
    private WebApplication? second;
    public Uri Origin { get; private set; } = null!;
    public Uri ReplicaOrigin { get; private set; } = null!;

    public static async Task<PageAuditHost> StartAsync(string root)
    {
        var host = new PageAuditHost();
        try
        {
            await host.environment.InitializeAsync();
            host.first = host.Create(root);
            await host.first.StartAsync();
            host.Origin = Address(host.first);
            host.second = host.Create(root);
            await host.second.StartAsync();
            host.ReplicaOrigin = Address(host.second);
            return host;
        }
        catch { await host.DisposeAsync(); throw; }
    }

    public HttpClient CreateClient(bool replica = false) => new(new HttpClientHandler
    { ServerCertificateCustomValidationCallback = TestHttps.Validate, AllowAutoRedirect = false, CookieContainer = new CookieContainer() })
    { BaseAddress = replica ? ReplicaOrigin : Origin, Timeout = TimeSpan.FromSeconds(30) };

    private WebApplication Create(string root)
    {
        var app = environment.CreateTestHost(root, 0, "http://127.0.0.1", builder =>
            builder.Services.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock)));
        app.MapPost("/__test/oct13/page-audit/seed", async (HttpContext http, DashboardSessions sessions, AccountService accounts) =>
        {
            var context = await AccountEndpoints.RequireAsync(http, sessions, accounts);
            if (context is null) return Results.Unauthorized();
            if (!Csrf.Validate(http, await http.Request.ReadFormAsync(http.RequestAborted))) return Results.BadRequest();
            var now = clock.GetUtcNow();
            await accounts.UpdateAccountAsync(context.Session, account =>
            {
                account.Notifications = Enumerable.Range(0, 12).Select(index => new NotificationRecord
                {
                    Id = "audit-notice-" + index,
                    OrganizationId = context.Organization!.Id,
                    ProjectId = context.Project!.Id,
                    Title = "Synthetic notice " + index,
                    Body = "Local page audit fixture.",
                    CreatedAt = now.AddMinutes(-index).ToOffset(TimeSpan.FromHours(-3))
                }).Concat(new[]
                {
                    new NotificationRecord { Id = "other-project-notice", OrganizationId = context.Organization!.Id, ProjectId = "other-project", Title = "Hidden project notice", CreatedAt = now },
                    new NotificationRecord { Id = "other-org-notice", OrganizationId = "other-organization", Title = "Hidden organization notice", CreatedAt = now }
                }).ToList();
            }, http.RequestAborted);
            await accounts.UpdateOrganizationAsync(context, organization =>
            {
                organization.AuditEvents = Enumerable.Range(0, 12).Select(index => new AuditRecord("audit-event-" + index,
                    "Synthetic actor", "audit.fixture", "Local target " + index, "database", now.AddMinutes(-index), Risk: "low")).Concat(new[]
                {
                    new AuditRecord("audit-unknown", "Synthetic actor", "audit.unknown", "Local unknown severity", "database", now.AddMinutes(-30)),
                    new AuditRecord("audit-boundary", "Synthetic actor", "audit.boundary", "Inclusive range edge", "database", now.AddDays(-1).ToOffset(TimeSpan.FromHours(-3)), Risk: "low"),
                    new AuditRecord("audit-before-boundary", "Synthetic actor", "audit.boundary", "Outside range edge", "database", now.AddDays(-1).AddTicks(-1), Risk: "low")
                }).ToList();
            }, http.RequestAborted);
            return Results.NoContent();
        });
        app.MapGet("/__test/oct13/page-audit/notifications", async (HttpContext http, DashboardSessions sessions, AccountService accounts) =>
        {
            var context = await AccountEndpoints.RequireAsync(http, sessions, accounts);
            return context is null ? Results.Unauthorized() : Results.Json(context.Account.Notifications.Select(item => new { item.Id, item.Status }));
        });
        return app;
    }

    private static Uri Address(WebApplication app) => new(app.Services.GetRequiredService<IServer>()
        .Features.Get<IServerAddressesFeature>()!.Addresses.Single().Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal));

    public async ValueTask DisposeAsync()
    {
        if (second is not null) { await second.StopAsync(); await second.DisposeAsync(); }
        if (first is not null) { await first.StopAsync(); await first.DisposeAsync(); }
    }

    private sealed class AuditClock : TimeProvider
    {
        private readonly DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
    }
}
