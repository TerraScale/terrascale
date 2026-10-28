using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Shared.Validation;

namespace TerraScale.Dashboard.Features.Organizations.Wizard;

public static class OrganizationWizard
{
    public static IEndpointRouteBuilder MapOrganizationWizard(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/dashboard/organization/new-organization", async (HttpContext http, DashboardSessions sessions,
            AccountService accounts, IWorkspacePlacement placement, StripeBilling payments) =>
        {
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null) return Results.Unauthorized();
            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            if (!Csrf.Validate(http, form)) return Results.StatusCode(403);
            try
            {
                if (form["step"] == "organization")
                {
                    var org = await accounts.CreateBusinessAsync(context.Session, form["name"].ToString(), http.RequestAborted);
                    context = await accounts.GetContextAsync(context.Session, http.RequestAborted, organizationId: org.Id);
                    return HttpRedirects.SeeOther(WorkspaceUrl.For(context, "/organization?newOrganization=payment&organizationId=" + Uri.EscapeDataString(org.Id), replace: true));
                }
                context = await SelectAsync(http, context, accounts, form["organizationID"].ToString());
                if (context.Organization?.Kind != "business" || context.Role != "owner") throw new AccountAccessException("Business organization ownership is required.");
                if (form["step"] == "payment-method")
                {
                    var plan = PlanCatalog.For(form["plan"].ToString());
                    if (plan.PriceCents <= 0) throw new OnboardingValidationException("Choose a paid plan for this business organization.");
                    var returnPath = WorkspaceUrl.For(context, "/organization?newOrganization=project&organizationId=" + Uri.EscapeDataString(context.Organization.Id), replace: true);
                    return HttpRedirects.SeeOther(await payments.StartCheckoutAsync(context, plan.Key, form["interval"].Count == 0 ? BillingPlan.DefaultInterval : form["interval"].ToString(), returnPath, http.RequestAborted));
                }
                if (form["step"] != "project") throw new AccountAccessException("Choose an organization setup step.", 400);
                if (!context.Organization.CanProvision(context.UserId)) throw new AccountAccessException("Confirm this organization's billing before creating its first project.", 409);
                if (context.Organization.Projects.Count > 0) return HttpRedirects.SeeOther(WorkspaceUrl.For(context, "/organization?tab=projects", replace: true));
                return await OrganizationEndpoints.MutateAsync(http, sessions, accounts, placement, "projects", scopedContext: context);
            }
            catch (Exception error) when (error is AccountAccessException or OnboardingValidationException)
            {
                var step = form["step"] == "payment-method" ? "payment" : form["step"] == "project" ? "project" : "organization";
                return HttpRedirects.SeeOther(WorkspaceUrl.For(context, "/organization?newOrganization=" + step + "&error=" + Uri.EscapeDataString(error.Message), replace: true));
            }
        });
        return endpoints;
    }

    public static async Task<AccountContext> PrepareAsync(HttpContext http, AccountContext context, AccountService accounts)
    {
        if (http.Request.Query["newOrganization"] is not { Count: > 0 }) return context;
        context = await SelectAsync(http, context, accounts, http.Request.Query["organizationId"].ToString());
        if (http.Request.Query["payment"] == "success" && context.Role == "owner")
        {
            await http.RequestServices.GetRequiredService<StripeBilling>().ConfirmAsync(context, http.RequestAborted);
            context = await AccountScope.RefreshAsync(accounts, context, http.RequestAborted, reload: true);
        }
        return context;
    }

    private static async Task<AccountContext> SelectAsync(HttpContext http, AccountContext context, AccountService accounts, string id)
    {
        if (id.Length == 0 || id == context.Organization?.Id) return context;
        return await accounts.GetContextAsync(context.Session, http.RequestAborted, organizationId: id);
    }
}
