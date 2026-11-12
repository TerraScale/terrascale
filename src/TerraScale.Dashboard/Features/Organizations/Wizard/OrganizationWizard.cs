using RazorSlices;
using StripeException = Stripe.StripeException;
using TerraScale.Dashboard.Common.UI;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Common.Validation;

namespace TerraScale.Dashboard.Features.Organizations.Wizard;

public static class OrganizationWizard
{
    internal static async Task<IResult> MutateAsync(HttpContext http, DashboardSessions sessions, AccountService accounts,
        IWorkspacePlacement placement, StripeBilling payments)
    {
        var context = await AccountRequest.RequireAsync(http, sessions, accounts);
        if (context is null)
        {
            return Results.Unauthorized();
        }

        var form = await FormRequests.ReadAsync(http, http.RequestAborted);
        if (!Csrf.Validate(http, form))
        {
            return RequestError.FormExpired(http, "/organization", Results.StatusCode(403));
        }

        try
        {
            if (form["step"] == "cancel-checkout")
            {
                var draftId = form["draft_id"].ToString();
                var draft = await payments.OrganizationDraftForCancellationAsync(context.UserId, draftId, http.RequestAborted);
                var result = await payments.CancelPendingOrganizationAsync(context.Session, http, draftId,
                    "Organization checkout canceled.", http.RequestAborted);
                if (result.OrganizationId is { } organizationId)
                {
                    return HttpRedirects.SeeOther("/organization?newOrganization=project&organizationId=" + Uri.EscapeDataString(organizationId));
                }
                if (draft is not null)
                {
                    http.Items[nameof(PendingOrganizationCheckout)] = draft;
                }
                return OrganizationView.Create(await OrganizationEndpoints.PageModelAsync(http, context, placement, wizardOverride: "payment"));
            }

            var name = AccountService.ValidName(form["name"].ToString());
            if (form["step"] == "organization")
            {
                return await NameStepAsync(http, context, placement, name);
            }

            if (form["step"] == "payment-method")
            {
                var plan = PlanCatalog.For(form["plan"].ToString());
                var interval = form["interval"].ToString() is { Length: > 0 } chosen ? chosen : plan.CheckoutInterval;
                return HttpRedirects.SeeOther(await payments.StartOrganizationCheckoutAsync(context.Session, name, plan.Key, interval, http.RequestAborted));
            }

            if (form["step"] != "project")
            {
                throw new AccountAccessException("Choose an organization setup step.", 400);
            }

            context = await SelectAsync(http, context, accounts, form["organizationID"].ToString());
            if (context.Organization?.Kind != "business" || context.Role != "owner")
            {
                throw new AccountAccessException("Business organization ownership is required.");
            }

            if (context.Organization.Projects.Count > 0)
            {
                return HttpRedirects.SeeOther(WorkspaceUrl.For(context, "/organization?tab=projects", replace: true));
            }

            await http.RequestServices.GetRequiredService<OrganizationOperations>().CreateProjectAsync(context, name,
                [.. form["regions"].Select(value => value ?? "").Where(value => value.Length > 0)], http.RequestAborted);
            return HttpRedirects.SeeOther(WorkspaceUrl.For(context, "/organization?tab=projects", replace: true));
        }
        catch (Exception error) when (error is AccountAccessException or OnboardingValidationException or StripeException or HttpRequestException or IOException or OperationCanceledException && !http.RequestAborted.IsCancellationRequested)
        {
            var step = form["step"].ToString() is "payment-method" or "cancel-checkout" ? "payment" : form["step"] == "project" ? "project" : "organization";
            return await RenderAsync(http, context, placement, step, form["name"].ToString(), form["plan"].ToString(),
                form["interval"].ToString(), error is StripeException or HttpRequestException or IOException or OperationCanceledException
                    ? "Checkout could not be updated. Try again." : error.Message,
                error is AccountAccessException access ? access.StatusCode : error is OnboardingValidationException ? 422 : 503, form);
        }
    }

    internal static async Task<IResult> NameStepAsync(HttpContext http, AccountContext context, IWorkspacePlacement placement, string name)
    {
        try
        {
            return HttpRedirects.SeeOther("/organization?newOrganization=payment&name=" + Uri.EscapeDataString(AccountService.BusinessName(context, name)));
        }
        catch (AccountAccessException error)
        {
            return await RenderAsync(http, context, placement, "organization", name, error: error.Message, status: error.StatusCode);
        }
    }

    private static async Task<RazorSlice> RenderAsync(HttpContext http, AccountContext context, IWorkspacePlacement placement, string step, string name,
        string plan = "", string interval = "", string error = "", int status = 200, IFormCollection? form = null)
    {
        var page = await OrganizationEndpoints.PageModelAsync(http, context, placement, error, wizardOverride: step);
        var slice = OrganizationView.Create(page with
        {
            OpenCreateOrganization = true,
            WizardStep = step,
            OrganizationDraftName = step == "project" ? "" : name,
            ProjectDraftName = step == "project" ? name : "",
            OrganizationDraftPlan = plan,
            OrganizationDraftInterval = interval,
            ProjectDraftRegions = form is null ? null : [.. form["regions"].Select(value => value ?? "").Where(page.Regions.Contains)]
        });
        slice.StatusCode = status;
        return slice;
    }

    public static Task<AccountContext> PrepareAsync(HttpContext http, AccountContext context, AccountService accounts)
    {
        return http.Request.Query["newOrganization"].Count > 0
            ? SelectAsync(http, context, accounts, http.Request.Query["organizationId"].ToString())
            : Task.FromResult(context);
    }

    private static async Task<AccountContext> SelectAsync(HttpContext http, AccountContext context, AccountService accounts, string id)
    {
        return id.Length == 0 || id == context.Organization?.Id ? context
            : await accounts.GetContextAsync(context.Session, http.RequestAborted, organizationId: id);
    }
}
