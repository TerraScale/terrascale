using TerraScale.Dashboard.Platform.Datastar;
using TerraScale.Dashboard.Common.UI;

namespace TerraScale.Dashboard.Features.Home
{
    public sealed record HomePage(ShellModel Shell, string Tab, string Range, HomeMeasurement Measurement)
    {
        public int Databases => Shell.Context.Project?.Databases.Count ?? 0;
        public int? Members => Shell.Context.Organization is { } organization && organization.ContainsMember(Shell.Context.UserId)
            ? organization.Members.Values.Count(item => item.Status == "active") : null;
        public string RegionScope => Shell.Context.Role.Length == 0 ? "project" : "organization";
        public int TotalNotifications => Shell.Context.Account.Notifications.Count(item => item.VisibleTo(Shell.Context));
        public static readonly string[] Ranges = ["1h", "24h", "7d", "30d"];
        public static string NormalizeRange(string? value)
        {
            return value is "1h" or "24h" or "7d" or "30d" ? value : "24h";
        }
    }

    public static class HomeEndpoints
    {
        public static IServiceCollection AddHomeFeatures(this IServiceCollection services)
        {
            return services.AddSingleton<IHomeMeasurements, TerraBaseHomeMeasurements>();
        }

        public static IEndpointRouteBuilder MapHome(this IEndpointRouteBuilder endpoints)
        {
            endpoints.MapGet("/", async Task<IResult> (HttpContext http, DashboardSessions sessions, AccountService accounts, IHomeMeasurements measurements) =>
            {
                var context = await AccountRequest.RequireAsync(http, sessions, accounts);
                if (context is null)
                {
                    return AccountRequest.LoginRedirect(http);
                }

                context = await AccountScope.RefreshAsync(accounts, context, http.RequestAborted, requireProject: context.Role.Length == 0);
                if (!context.Account.OnboardingCompleted)
                {
                    return Results.Redirect("/onboarding");
                }

                if (context.Organization is null)
                {
                    return Results.Problem("Selected organization is unavailable.", statusCode: 403);
                }

                if (context.Project is null)
                {
                    return Results.Redirect(context.Organization.Kind == "business" && !context.Organization.CanProvision(context.UserId) ? "/organization?newOrganization=payment" : "/organization?tab=projects&newProject=1");
                }

                var range = HomePage.NormalizeRange(http.Request.Query["range"]);
                var tab = http.Request.Query["tab"] == "connect" ? "connect" : "overview";
                var data = tab == "overview" ? await measurements.ReadPageAsync(context, range, http.RequestAborted) : new HomeMeasurement();
                return HomeView.Create(new(ShellModel.For(http, context, tab == "connect" ? "Connect" : "Overview"), tab, range, data));
            });
            endpoints.MapGet("/overview/regions", async Task<IResult> (HttpContext http, DashboardSessions sessions, AccountService accounts, IHomeMeasurements measurements) =>
            {
                var context = await AccountRequest.RequireAsync(http, sessions, accounts);
                if (context is null)
                {
                    return Results.Unauthorized();
                }

                context = await AccountScope.RefreshAsync(accounts, context, http.RequestAborted, requireProject: context.Role.Length == 0);
                if (!http.Request.Query.ContainsKey("organizationId") || !http.Request.Query.ContainsKey("projectId"))
                {
                    return Results.BadRequest("Workspace identity is required.");
                }

                if (!context.Account.OnboardingCompleted || context.Organization is null || context.Project is null)
                {
                    return Results.Problem("Selected workspace is unavailable.", statusCode: 403);
                }

                var data = await measurements.ReadRegionsAsync(context, http.RequestAborted);
                await DatastarEvents.PatchElementsAsync(http, HomeRegionsView.Create(new(ShellModel.For(http, context, "Overview"), "overview", "24h", data)));
                return Results.Empty;
            });
            return endpoints;
        }
    }
}
