using RazorSlices;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Platform.Datastar;
using TerraScale.Dashboard.UI;

namespace TerraScale.Dashboard.Features.Organizations;

public sealed partial record OrganizationPage(ShellModel Shell, string Tab, IReadOnlyList<string> Regions, string Error = "", bool OpenCreateProject = false, bool OpenCreateOrganization = false,
    OrganizationAccessView? Access = null, string InvitationLink = "", OrganizationFilters? Filters = null, string WizardStep = "", bool PaymentCanceled = false,
    BillingPage? Billing = null);

public static class OrganizationEndpoints
{
    public static IServiceCollection AddOrganizationFeatures(this IServiceCollection services) => services
        .AddSingleton<IOrganizationAccess, TsOrganizationAccess>()
        .AddSingleton<IInvitationGateway, TsInvitationGateway>().AddSingleton<InvitationService>()
        .AddSingleton<IOrganizationLifecycle, TsOrganizationLifecycle>().AddSingleton<OrganizationLifecycleService>()
        .AddSingleton<OrganizationOperations>();
    public static IEndpointRouteBuilder MapOrganizations(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/organization", async (HttpContext http, DashboardSessions sessions, AccountService accounts, IWorkspacePlacement placement,
            StripeBilling billing, SpendingService spending, TimeProvider time, InternalBillingService internalBilling) =>
        {
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null) return AccountRequest.LoginRedirect(http);
            if (http.Request.Query["tab"] == "billing" && http.Request.Query["payment"] == "success" && context.Role == "owner")
            {
                await http.RequestServices.GetRequiredService<TerraScale.Dashboard.Features.Billing.StripeBilling>().ConfirmAsync(context, http.RequestAborted);
                context = await accounts.GetContextAsync(context.Session, http.RequestAborted);
            }
            context = await OrganizationWizard.PrepareAsync(http, context, accounts);
            var page = await PageModelAsync(http, context, placement);
            if (page.Tab == "billing" && context.Organization is not null)
            {
                try { page = page with { Billing = await BillingEndpoints.LoadAsync(page.Shell, billing, spending, http.Request.Query, time.GetUtcNow(), http.RequestAborted, internalBilling) }; }
                catch (AccountAccessException error) { return Results.Text(error.Message, statusCode: error.StatusCode); }
                catch (Exception error) when (error is IOException or HttpRequestException or Stripe.StripeException || error is OperationCanceledException && !http.RequestAborted.IsCancellationRequested)
                { return Results.Text("Billing information could not be loaded. Reload billing.", statusCode: 503); }
            }
            if (http.Request.Headers["Datastar-Request"] == "true" && page.Tab is "members" or "invitations")
            {
                await DatastarEvents.PatchElementsAsync(http, OrganizationAccessPage.Create(page));
                return Results.Empty;
            }
            return OrganizationView.Create(page);
        });
        endpoints.MapGet("/api/v1/dashboard/context", async (HttpContext http, DashboardSessions sessions, AccountService accounts) =>
        {
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null) return Results.Unauthorized();
            return Results.Json(AccountContextView.From(context));
        });
        foreach (var route in new[] { "rename", "projects", "create" })
        {
            var operation = route;
            endpoints.MapPost("/api/v1/dashboard/organization/" + route,
                async (HttpContext http, DashboardSessions sessions, AccountService accounts, IWorkspaceGateway gateway, IWorkspacePlacement placement) =>
                    await MutateAsync(http, sessions, accounts, gateway, placement, operation));
        }
        endpoints.MapPost("/api/v1/dashboard/organization/projects/{projectId}/rename",
            async (HttpContext http, DashboardSessions sessions, AccountService accounts, IWorkspaceGateway gateway, IWorkspacePlacement placement, string projectId) =>
                await MutateAsync(http, sessions, accounts, gateway, placement, "rename-project", projectId));
        foreach (var action in new[] { "invite", "role", "remove", "ownership", "ownership/confirm", "ownership/cancel" })
        {
            var operation = action;
            endpoints.MapPost("/api/v1/dashboard/organization/members/" + action,
                async (HttpContext http, DashboardSessions sessions, AccountService accounts, IOrganizationAccess access, IWorkspacePlacement placement) =>
                    await AccessMutationAsync(http, sessions, accounts, access, placement, operation));
        }
        endpoints.MapPost("/api/v1/dashboard/organization/invitations/{id}/revoke",
            async (HttpContext http, DashboardSessions sessions, AccountService accounts, IOrganizationAccess access, IWorkspacePlacement placement, string id) =>
                await AccessMutationAsync(http, sessions, accounts, access, placement, "revoke-invitation", id));
        endpoints.MapPost("/api/v1/access/invitations", async (HttpContext http, DashboardSessions sessions, AccountService accounts, IOrganizationAccess access, IWorkspacePlacement placement) =>
            await AccessMutationAsync(http, sessions, accounts, access, placement, "invite"));
        foreach (var operation in new[] { "revoke", "resend" })
        {
            var action = operation;
            endpoints.MapPost("/api/v1/access/invitations/{id}/" + action, async (HttpContext http, DashboardSessions sessions, AccountService accounts, IOrganizationAccess access, IWorkspacePlacement placement, string id) =>
                await AccessMutationAsync(http, sessions, accounts, access, placement, action + "-invitation", id));
        }
        foreach (var operation in new[] { "role", "remove" })
        {
            var action = operation;
            endpoints.MapPost("/api/v1/dashboard/organization/members/{id}/" + action, async (HttpContext http, DashboardSessions sessions, AccountService accounts, IOrganizationAccess access, IWorkspacePlacement placement, string id) =>
                await AccessMutationAsync(http, sessions, accounts, access, placement, action, id));
        }
        endpoints.MapPost("/api/v1/access/members/{id}/role", async (HttpContext http, DashboardSessions sessions, AccountService accounts, IOrganizationAccess access, IWorkspacePlacement placement, string id) =>
            await AccessMutationAsync(http, sessions, accounts, access, placement, "role", id));
        endpoints.MapPost("/api/v1/access/members/{id}/revoke", async (HttpContext http, DashboardSessions sessions, AccountService accounts, IOrganizationAccess access, IWorkspacePlacement placement, string id) =>
            await AccessMutationAsync(http, sessions, accounts, access, placement, "remove", id));
        endpoints.MapPost("/api/v1/dashboard/organization/leave", async (HttpContext http, DashboardSessions sessions, AccountService accounts, OrganizationLifecycleService lifecycle, IWorkspacePlacement placement) =>
            await LifecycleAsync(http, sessions, accounts, lifecycle, placement, "leave"));
        endpoints.MapPost("/api/v1/dashboard/organization/deletion", async (HttpContext http, DashboardSessions sessions, AccountService accounts, OrganizationLifecycleService lifecycle, IWorkspacePlacement placement) =>
            await LifecycleAsync(http, sessions, accounts, lifecycle, placement, "delete"));
        endpoints.MapPost("/api/v1/dashboard/organization/projects/{id}/delete", async (HttpContext http, DashboardSessions sessions, AccountService accounts, OrganizationLifecycleService lifecycle, IWorkspacePlacement placement, string id) =>
            await LifecycleAsync(http, sessions, accounts, lifecycle, placement, "delete-project", id));
        endpoints.MapOrganizationWizard();
        endpoints.MapInvitations();
        endpoints.MapOrganizationAudit();
        return endpoints;
    }

    private static async Task<RazorSlice> PageAsync(HttpContext http, AccountContext context, IWorkspacePlacement placement, string error = "", int status = 200)
    {
        var slice = OrganizationView.Create(await PageModelAsync(http, context, placement, error));
        slice.StatusCode = status;
        return slice;
    }

    private static async Task<OrganizationPage> PageModelAsync(HttpContext http, AccountContext context, IWorkspacePlacement placement, string error = "")
    {
        var tab = http.Request.Query["tab"].ToString();
        if (tab is not ("projects" or "members" or "invitations" or "audit" or "billing")) tab = "organization";
        var regions = tab == "projects" || http.Request.Query["newOrganization"] == "project" || http.Request.Query["newProject"] == "1"
            ? await placement.RegionsAsync(http.RequestAborted) : [];
        var access = context.Organization is not null ? await http.RequestServices.GetRequiredService<IOrganizationAccess>().ReadAsync(context, http.RequestAborted) : null;
        var wizard = http.Request.Query["newOrganization"].ToString();
        if (wizard is not ("organization" or "payment" or "project")) wizard = "";
        if (wizard.Length > 0 && wizard != "organization")
        {
            if (context.Organization?.Kind != "business" || context.Role != "owner") wizard = "organization";
            else if (!context.Organization.CanProvision(context.UserId)) wizard = "payment";
            else wizard = "project";
        }
        if (error.Length == 0 && http.Request.Query["error"].ToString() is { Length: > 0 and <= 500 } queryError) error = queryError;
        return new(ShellModel.For(http, context, "Organization"), tab, regions, error,
            http.Request.Query["newProject"] == "1", wizard.Length > 0, access, Filters: OrganizationFilters.For(http), WizardStep: wizard, PaymentCanceled: http.Request.Query["payment"] == "canceled");
    }

    private static async Task<IResult> AccessMutationAsync(HttpContext http, DashboardSessions sessions, AccountService accounts, IOrganizationAccess access, IWorkspacePlacement placement, string operation, string? id = null)
    {
        var context = await AccountRequest.RequireAsync(http, sessions, accounts);
        if (context is null) return Results.Unauthorized();
        var form = await http.Request.ReadFormAsync(http.RequestAborted);
        if (!Csrf.Validate(http, form)) return Results.BadRequest("Invalid form token.");
        try
        {
            if (operation is "invite" or "resend-invitation")
            {
                var invitations = http.RequestServices.GetRequiredService<InvitationService>();
                var created = operation == "invite" ? await invitations.CreateAsync(context, form["email"].ToString(), form["role"].ToString() is { Length: > 0 } role ? role : "member", http.RequestAborted)
                    : await invitations.ResendAsync(context, id!, http.RequestAborted);
                context = await accounts.GetContextAsync(context.Session, http.RequestAborted);
                var view = await access.ReadAsync(context, http.RequestAborted);
                http.Response.Headers.CacheControl = "no-store";
                http.Response.Headers["Referrer-Policy"] = "strict-origin";
                return OrganizationView.Create(new(ShellModel.For(http, context, "Organization"), http.Request.Query["tab"] == "invitations" ? "invitations" : "members", [], Access: view,
                    InvitationLink: $"{http.Request.Scheme}://{http.Request.Host}/invitations/{Uri.EscapeDataString(created.Token)}"));
            }
            if (operation == "revoke-invitation") await http.RequestServices.GetRequiredService<InvitationService>().RevokeAsync(context, id!, http.RequestAborted);
            else
            {
                var operations = http.RequestServices.GetRequiredService<OrganizationOperations>();
                await operations.MutateMemberAsync(context, operation switch
                {
                    "role" => "role",
                    "remove" => "remove",
                    "ownership" => "start",
                    "ownership/confirm" => "confirm",
                    _ => "cancel"
                }, id ?? form["userID"].ToString(), form["role"].ToString(), http.RequestAborted);
            }
            return HttpRedirects.SeeOther("/organization?tab=members");
        }
        catch (AccountAccessException error) { return await PageAsync(http, context, placement, error.Message, error.StatusCode); }
    }

    private static async Task<IResult> LifecycleAsync(HttpContext http, DashboardSessions sessions, AccountService accounts, OrganizationLifecycleService lifecycle, IWorkspacePlacement placement, string operation, string? projectId = null)
    {
        var context = await AccountRequest.RequireAsync(http, sessions, accounts);
        if (context is null) return Results.Unauthorized();
        var form = await http.Request.ReadFormAsync(http.RequestAborted);
        if (!Csrf.Validate(http, form)) return Results.StatusCode(403);
        try
        {
            if (operation == "leave") await lifecycle.LeaveAsync(context, http.RequestAborted);
            else if (operation == "delete-project") await lifecycle.DeleteProjectAsync(context, projectId!, form["confirmation"].ToString(), http.RequestAborted);
            else await lifecycle.RequestDeletionAsync(context, form["confirmation"].ToString(), http.RequestAborted);
            return HttpRedirects.SeeOther(operation == "delete-project" ? "/organization?tab=projects" : "/organization");
        }
        catch (AccountAccessException error) { return await PageAsync(http, context, placement, error.Message, error.StatusCode); }
    }

    internal static async Task<IResult> MutateAsync(HttpContext http, DashboardSessions sessions, AccountService accounts, IWorkspaceGateway gateway, IWorkspacePlacement placement, string operation, string? projectId = null)
    {
        var context = await AccountRequest.RequireAsync(http, sessions, accounts);
        if (context is null) return Results.Unauthorized();
        var form = await http.Request.ReadFormAsync(http.RequestAborted);
        if (!Csrf.Validate(http, form)) return Results.BadRequest("Invalid form token.");
        try
        {
            var name = AccountService.ValidName(form["name"].ToString());
            if (operation == "create")
            {
                await accounts.CreateBusinessAsync(context.Session, name, http.RequestAborted);
                return HttpRedirects.SeeOther("/organization?tab=billing");
            }
            var operations = http.RequestServices.GetRequiredService<OrganizationOperations>();
            if (operation == "rename")
            {
                await operations.RenameAsync(context, name, http.RequestAborted);
                return HttpRedirects.SeeOther("/organization");
            }
            var regions = form["regions"].Select(item => item ?? "").Where(item => item.Length > 0).ToArray();
            var primary = form["region"].ToString();
            var backup = form["backupRegion"].ToString();
            if (regions.Length == 0 && primary.Length > 0) regions = [primary];
            if (regions.Length == 0) throw new AccountAccessException("Choose at least one serving region.", 422);
            if (operation == "projects")
                await operations.CreateProjectAsync(context, name, primary, backup, regions, http.RequestAborted);
            else
            {
                var project = context.Organization?.Projects.GetValueOrDefault(projectId ?? "") ?? throw new AccountAccessException("Project is unavailable.", 404);
                await operations.UpdateProjectAsync(context with { Project = project }, name, primary, backup, regions, http.RequestAborted);
            }
            return HttpRedirects.SeeOther("/organization?tab=projects");
        }
        catch (AccountAccessException error) { return await PageAsync(http, context, placement, error.Message, error.StatusCode); }
    }
}
