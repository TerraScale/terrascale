using System.Diagnostics;
using System.Text.Json;
using TerraScale.Dashboard.Common.UI;
using TerraScale.Dashboard.Errors;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Platform.Datastar;

namespace TerraScale.Dashboard.Features.Notifications
{
    using NotFound = TerraScale.Dashboard.Errors.NotFound;

    public sealed record AlertsPage(ShellModel Shell, IReadOnlyList<AlertRule> Rules, string Error = "", AlertRule? Created = null, string? Secret = null)
    {
        public bool CanManage => Shell.Context.Role == "owner";
        public ProjectRecord Project => Shell.Context.Project!;
        public string Database(AlertRule rule)
        {
            return rule.DatabaseId is null ? "Every database" : Project.Databases.GetValueOrDefault(rule.DatabaseId)?.Name ?? "Deleted database";
        }

        public static string Metric(string metric)
        {
            return metric switch
            {
                "p95_latency_ms" => "p95 latency (ms)",
                "error_rate_percent" => "Error rate (%)",
                "storage_gb" => "Storage (GB)",
                _ => "Operation units (% of plan)"
            };
        }
    }

    public static class AlertEndpoints
    {
        internal static async Task<IResult> PageAsync(HttpContext http, DashboardSessions sessions, AccountService accounts, AlertRules rules, AuthFlows flows)
        {
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null)
            {
                return AccountRequest.LoginRedirect(http);
            }

            if (context.Project is null)
            {
                return HttpRedirects.SeeOther("/");
            }

            var handoff = await flows.TakeAlertSecretAsync(http, context);
            return await PageAsync(http, context, rules, created: handoff is null ? null : new AlertRule("", handoff.RuleName, "", 0, null, false, null, default), secret: handoff?.Secret);
        }

        internal static async Task<IResult> MutateAsync(HttpContext http, DashboardSessions sessions, AccountService accounts, AlertRules rules, AuthFlows flows, string action)
        {
            http.Response.Headers.CacheControl = "no-store";
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null)
            {
                return TypedResults.Unauthorized();
            }

            var form = await FormRequests.ReadAsync(http, http.RequestAborted);
            if (!Csrf.Validate(http, form))
            {
                return RequestError.FormExpired(http, "/notifications/alerts", TypedResults.BadRequest("Invalid form token."));
            }

            try
            {
                var id = http.Request.RouteValues["id"]?.ToString() ?? "";
                if (action == "delete")
                {
                    var deleted = await rules.DeleteAsync(context, id, http.RequestAborted);
                    if (deleted is not Done)
                    {
                        return deleted switch
                        {
                            NotFound error => await PageAsync(http, context, rules, error.Message!, 404),
                            Forbidden error => await PageAsync(http, context, rules, error.Message, 403),
                            Validation error => await PageAsync(http, context, rules, error.Message, 422),
                            Done => throw new UnreachableException(),
                        };
                    }
                }
                else if (action == "threshold")
                {
                    var updated = await rules.UpdateThresholdAsync(context, id, form["threshold"], http.RequestAborted);
                    if (updated is not Done)
                    {
                        return updated switch
                        {
                            NotFound error => await PageAsync(http, context, rules, error.Message!, 404),
                            Forbidden error => await PageAsync(http, context, rules, error.Message, 403),
                            Validation error => await PageAsync(http, context, rules, error.Message, 422),
                            Done => throw new UnreachableException(),
                        };
                    }
                }
                else
                {
                    var created = await rules.CreateAsync(context, form, http.RequestAborted);
                    if (created is not AlertRuleWithSecret rule)
                    {
                        return created switch
                        {
                            Validation error => await PageAsync(http, context, rules, error.Message, 422),
                            Conflict error => await PageAsync(http, context, rules, error.Message, 409),
                            NotFound error => await PageAsync(http, context, rules, error.Message!, 404),
                            Forbidden error => await PageAsync(http, context, rules, error.Message, 403),
                            AlertRuleWithSecret => throw new UnreachableException(),
                        };
                    }

                    if (rule.Secret is not null)
                    {
                        await flows.HoldAlertSecretAsync(http, context, rule.Rule.Name, secret: rule.Secret);
                    }

                    return HttpRedirects.SeeOther("/notifications/alerts");
                }
            }
            catch (ArgumentException error) { return await PageAsync(http, context, rules, error.Message, 422); }
            if (http.Request.IsDatastarRequest)
            {
                return await PageAsync(http, context, rules);
            }

            return HttpRedirects.SeeOther("/notifications/alerts");
        }

        private static async Task<IResult> PageAsync(HttpContext http, AccountContext context, AlertRules rules, string error = "", int status = 200, AlertRule? created = null, string? secret = null)
        {
            if (context.Project is null)
            {
                return TypedResults.Text(error.Length > 0 ? error : "Select a project.", statusCode: status == 200 ? 404 : status);
            }

            IReadOnlyList<AlertRule> items = [];
            try { items = await rules.ListAsync(context.Project.Id, http.RequestAborted); }
            catch (Exception failure) when (failure is IOException or JsonException or HttpRequestException)
            {
                if (error.Length == 0)
                {
                    (error, status) = ("Alert rules are unavailable. Reload to retry.", 503);
                }
            }
            var model = new AlertsPage(ShellModel.For(http, context, "Alerts"), items, error, created, secret);
            if (http.Request.IsDatastarRequest)
            {
                await DatastarEvents.PatchElementsAsync(http, AlertsContent.Create(model));
                return TypedResults.Empty;
            }

            var page = AlertsView.Create(model);
            page.StatusCode = status;
            return page;
        }
    }
}
