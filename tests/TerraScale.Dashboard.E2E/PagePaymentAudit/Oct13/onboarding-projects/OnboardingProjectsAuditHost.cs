using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.TerraBase.Contracts;
using TerraScale.TerraBase.Contracts.Documents;

namespace TerraScale.Dashboard.E2E.PagePaymentAudit.Oct13.OnboardingProjects
{
    // Local test-only host serves committed JS bytes without writing generated shared assets.
    internal sealed class OnboardingProjectsAuditHost(WebApplication app, string origin) : IAsyncDisposable
    {
        public string Origin { get; } = origin;
        public static async Task<OnboardingProjectsAuditHost> StartAsync(string root)
        {
            var environment = new DemoEnvironment();
            await environment.InitializeAsync();
            var app = environment.CreateTestHost(root, 0, "http://127.0.0.1", builder =>
                builder.Services.AddSingleton<IStartupFilter>(new AuditAssets(root)));
            try
            {
                await app.StartAsync();
                var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
                return new(app, address.Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal));
            }
            catch { await app.DisposeAsync(); throw; }
        }
        // Test admission permits more projects than Free allows; it does not simulate a paid checkout.
        public async Task PlanAsync(string organizationId, string planId)
        {
            var state = app.Services.GetRequiredService<IDashboardState>();
            var current = await state.GetAsync(AccountService.OrganizationsCollection, organizationId, default)
                ?? throw new InvalidOperationException("The fixture plan targets an existing organization");
            var organization = current.Document.Deserialize<OrganizationRecord>()!;
            organization.PlanId = planId;
            await state.PutAsync(AccountService.OrganizationsCollection, organizationId, JsonSerializer.SerializeToElement(organization),
                TerraBasePrecondition.Matches(current.Version), default);
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }

        private sealed class AuditAssets(string root) : IStartupFilter
        {
            public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
            {
                return app =>
            {
                app.Use(async (http, handler) =>
                {
                    var source = http.Request.Path.Value switch
                    {
                        "/js/datastar.js" => "vendor/datastar.js",
                        "/js/datastar.js.map" => "vendor/datastar.js.map",
                        "/js/button-loading.js" => "button-loading.js",
                        "/js/workspace.js" => "workspace.js",
                        _ => null
                    };
                    if (source is not null)
                    {
                        http.Response.ContentType = source.EndsWith(".map", StringComparison.Ordinal) ? "application/json" : "text/javascript";
                        await http.Response.SendFileAsync(Path.Combine(root, "src/TerraScale.Dashboard/web/assets/js", source), http.RequestAborted);
                        return;
                    }
                    await handler(http);
                });
                next(app);
            };
            }
        }
    }
}
