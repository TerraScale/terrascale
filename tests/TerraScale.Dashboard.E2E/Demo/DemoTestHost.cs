using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Platform.Datastar;

namespace TerraScale.Dashboard.E2E
{
    // The suite's synthetic application host plus the suite's scenario and probe routes and reference fixtures.
    public static class DemoTestHost
    {
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DemoEnvironment, TestStepUpGateway> StepUps = new();
        public static WebApplication CreateTestHost(this DemoEnvironment environment, string root, int port, string origin, Action<WebApplicationBuilder>? configureScenario = null)
        {
            var app = environment.CreateHost(Path.Combine(root, "src", "TerraScale.Dashboard"), port, origin, builder =>
            {
                TestHttps.Configure(builder);
                builder.Services.AddSingleton<TestInitializerDiagnostics>();
                builder.Services.AddSingleton(services =>
                {
                    var gateway = StepUps.GetValue(environment, _ => new(services.GetRequiredService<TimeProvider>(), environment.Auth.ResolveAccessToken));
                    environment.ApiKeys.StepUp = gateway;
                    environment.Authentication.StepUp = gateway;
                    environment.Workspace.AuthorizeDeletion = gateway.AuthorizeDeletion;
                    return gateway;
                });
                builder.Services.AddSingleton<IAuthStepUpGateway>(services => services.GetRequiredService<TestStepUpGateway>());
                configureScenario?.Invoke(builder);
            });
            app.Services.GetRequiredService<TestInitializerDiagnostics>().Attach(app);
            app.MapGet("/__test/initializer-error", (Func<IResult>)(() =>
            {
                try { throw new InvalidOperationException("Synthetic exception detail"); }
                catch (InvalidOperationException error) { throw new TypeInitializationException("SyntheticInitializer", error); }
            }));
            app.Services.GetRequiredService<TestStepUpGateway>().Map(app);
            app.MapGet("/__test/unexpected-error", (Func<IResult>)(() => throw new InvalidOperationException("Synthetic exception detail")));
            app.MapGet("/__test/session", async (HttpContext context, DashboardSessions sessions) =>
                await sessions.ReadAsync(context) is { } session ? Results.Json(new { session.UserId }) : (IResult)Results.Unauthorized());
            app.MapGet("/__test/signals", async (HttpContext context) =>
            {
                await DatastarEvents.PatchSignalsAsync(context, new { count = 1 });
                await DatastarEvents.PatchSignalsAsync(context, new { count = 2 });
            });
            app.MapGet("/__test/elements", (HttpContext context) => DatastarEvents.PatchElementsAsync(context, TerraScale.Dashboard.Common.UI.Slices.RegionMap.Create()));
            BillingWebhookScenario.MapRoutes(app);
            InvitationExpiryScenario.MapRoutes(app);
            OrganizationProviderScenario.MapRoutes(app);
            return app;
        }

        public static async Task<ReferencePersonas> InitializeReferenceAsync(this DemoEnvironment environment, string root)
        {
            await environment.InitializeAsync();
            return await ReferenceFixtures.InitializeAsync(root, environment.State, environment.Workspace, environment.Auth.RegisterFixturePrincipal, environment.Databases.SeedFixtureDatabase, environment.Documents);
        }
    }
}
