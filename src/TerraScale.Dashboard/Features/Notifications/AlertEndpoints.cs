using System.Text.Json;
using TerraScale.Dashboard.Shared.UI;

namespace TerraScale.Dashboard.Features.Notifications;

public sealed record AlertsPage(ShellModel Shell, IReadOnlyList<AlertRule> Rules, string Error = "", AlertRule? Created = null, string? Secret = null)
{
    public bool CanManage => Shell.Context.Role == "owner";
    public ProjectRecord Project => Shell.Context.Project!;
    public string Database(AlertRule rule) => rule.DatabaseId is null ? "Every database" : Project.Databases.GetValueOrDefault(rule.DatabaseId)?.Name ?? "Deleted database";
    public static string Metric(string metric) => metric switch
    {
        "p95_latency_ms" => "p95 latency (ms)",
        "error_rate_percent" => "Error rate (%)",
        "storage_gb" => "Storage (GB)",
        _ => "Operation units (% of plan)"
    };
}

public static class AlertEndpoints
{
    public static IEndpointRouteBuilder MapAlerts(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/notifications/alerts", async (HttpContext http, DashboardSessions sessions, AccountService accounts, AlertRules rules) =>
        {
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null) return AccountRequest.LoginRedirect(http);
            if (context.Project is null) return HttpRedirects.SeeOther("/");
            return await PageAsync(http, context, rules);
        });
        foreach (var action in new[] { "", "delete", "threshold" })
        {
            endpoints.MapPost(action.Length == 0 ? "/api/v1/notifications/alerts" : "/api/v1/notifications/alerts/{id}/" + action, async (HttpContext http, DashboardSessions sessions, AccountService accounts, AlertRules rules) =>
            {
                http.Response.Headers.CacheControl = "no-store";
                var context = await AccountRequest.RequireAsync(http, sessions, accounts);
                if (context is null) return Results.Unauthorized();
                var form = await http.Request.ReadFormAsync(http.RequestAborted);
                if (!Csrf.Validate(http, form)) return Results.BadRequest("Invalid form token.");
                try
                {
                    var id = http.Request.RouteValues["id"]?.ToString() ?? "";
                    if (action == "delete") await rules.DeleteAsync(context, id, http.RequestAborted);
                    else if (action == "threshold") await rules.UpdateThresholdAsync(context, id, form["threshold"], http.RequestAborted);
                    else
                    {
                        var (rule, secret) = await rules.CreateAsync(context, form, http.RequestAborted);
                        // The signing secret is in this response only.
                        http.Response.Headers["Referrer-Policy"] = "no-referrer";
                        return await PageAsync(http, context, rules, created: rule, secret: secret, status: 201);
                    }
                }
                catch (AccountAccessException error) { return await PageAsync(http, context, rules, error.Message, error.StatusCode); }
                catch (ArgumentException error) { return await PageAsync(http, context, rules, error.Message, 422); }
                return HttpRedirects.SeeOther("/notifications/alerts");
            });
        }
        return endpoints;
    }

    private static async Task<IResult> PageAsync(HttpContext http, AccountContext context, AlertRules rules, string error = "", int status = 200, AlertRule? created = null, string? secret = null)
    {
        if (context.Project is null) return Results.Text(error.Length > 0 ? error : "Select a project.", statusCode: status == 200 ? 404 : status);
        IReadOnlyList<AlertRule> items = [];
        try { items = await rules.ListAsync(context.Project.Id, http.RequestAborted); }
        catch (Exception failure) when (failure is IOException or JsonException or HttpRequestException)
        {
            if (error.Length == 0) (error, status) = ("Alert rules are unavailable. Reload to retry.", 503);
        }
        var page = AlertsView.Create(new AlertsPage(ShellModel.For(http, context, "Alerts"), items, error, created, secret));
        page.StatusCode = status;
        return page;
    }
}
