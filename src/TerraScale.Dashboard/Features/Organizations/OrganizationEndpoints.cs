using RazorSlices;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Features.Organizations.Audit;
using TerraScale.Dashboard.Features.Organizations.Invitations;
using TerraScale.Dashboard.Features.Organizations.Members;
using TerraScale.Dashboard.Features.Organizations.Sso;
using TerraScale.Dashboard.Features.Organizations.Wizard;
using TerraScale.Dashboard.Platform.Datastar;
using TerraScale.Dashboard.Common.UI;

namespace TerraScale.Dashboard.Features.Organizations
{
    public sealed partial record OrganizationPage(ShellModel Shell, string Tab, IReadOnlyList<string> Regions, string Error = "", bool OpenCreateProject = false, bool OpenCreateOrganization = false,
        OrganizationAccessView? Access = null, string InvitationLink = "", OrganizationFilters? Filters = null, string WizardStep = "", bool PaymentCanceled = false,
        BillingPage? Billing = null, string ProjectDraftName = "", IReadOnlyList<string>? ProjectDraftRegions = null,
        string ProjectDraftPrimaryRegion = "", string ProjectDraftBackupRegion = "", string Status = "", bool? RequireMfa = null, OrganizationSsoView? Sso = null, string Notice = "", int NoticeStatus = 0);

    public static class OrganizationEndpoints
    {
        public static IServiceCollection AddOrganizationFeatures(this IServiceCollection services)
        {
            return services
            .AddSingleton<IOrganizationAccess, TsOrganizationAccess>()
            .AddSingleton<InvitationService>()
            .AddSingleton<OrganizationSsoService>()
            .AddSingleton<OrganizationLifecycleService>()
            .AddSingleton<OrganizationOperations>()
            .AddSingleton<Projects.ProjectGrantService>()
            .AddSingleton<Projects.ProjectInvitationService>();
        }

        public static IEndpointRouteBuilder MapOrganizations(this IEndpointRouteBuilder endpoints)
        {
            endpoints.MapGet("/organization", async Task<IResult> (HttpContext http, DashboardSessions sessions, AccountService accounts, IWorkspacePlacement placement,
                StripeBilling billing, SpendingService spending, TimeProvider time, InternalBillingService internalBilling) =>
            {
                var context = await AccountRequest.RequireAsync(http, sessions, accounts);
                if (context is null)
                {
                    return AccountRequest.LoginRedirect(http);
                }

                if (context.Organization is not null && context.Role.Length == 0)
                {
                    return Notice(http, context, "organization", "Organization membership is required. Ask an owner or admin for access.", 403);
                }

                if (http.Request.Query["tab"] == "billing" && http.Request.Query["payment"] == "success" && context.Role == "owner")
                {
                    try
                    {
                        await billing.ConfirmAsync(context, http.RequestAborted);
                        context = await AccountScope.RefreshAsync(accounts, context, http.RequestAborted, reload: true);
                    }
                    catch (AccountAccessException error) { return Notice(http, context, "billing", error.Message + (error.StatusCode == 403 ? " Ask an owner or admin for access." : ""), error.StatusCode); }
                    catch (Exception error) when (error is IOException or HttpRequestException or Stripe.StripeException || error is OperationCanceledException && !http.RequestAborted.IsCancellationRequested)
                    { return Notice(http, context, "billing", "Billing information could not be loaded. Your organization and projects are still available.", 503); }
                }
                else if (http.Request.Query["tab"] == "billing" && context.Organization is not null)
                {
                    // A provider failure leaves the stored state; the page still loads and the next reload retries.
                    try { if (await billing.ReconcileCheckoutAsync(context, http.RequestAborted))
                        {
                            context = await AccountScope.RefreshAsync(accounts, context, http.RequestAborted, reload: true);
                        }
                    }
                    catch (Exception error) when (error is AccountAccessException or IOException or HttpRequestException or Stripe.StripeException || error is OperationCanceledException && !http.RequestAborted.IsCancellationRequested) { }
                }
                if (http.Request.Query["tab"] == "audit" && !context.CanManage)
                {
                    return Notice(http, context, "audit", "Organization management is required to view audit events. Ask an owner or admin for access.", 403);
                }

                context = await OrganizationWizard.PrepareAsync(http, context, accounts);
                var page = await PageModelAsync(http, context, placement);
                if (page.Tab == "billing" && context.Organization is not null)
                {
                    try { page = page with { Billing = await BillingEndpoints.LoadAsync(page.Shell, billing, spending, http.Request.Query, time.GetUtcNow(), http.RequestAborted, internalBilling) }; }
                    catch (AccountAccessException error) { return Notice(http, context, "billing", error.Message + (error.StatusCode == 403 ? " Ask an owner or admin for access." : ""), error.StatusCode); }
                    catch (Exception error) when (error is IOException or HttpRequestException or Stripe.StripeException || error is OperationCanceledException && !http.RequestAborted.IsCancellationRequested)
                    { return Notice(http, context, "billing", "Billing information could not be loaded. Your organization and projects are still available.", 503); }
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
                if (context is null)
                {
                    return Results.Unauthorized();
                }

                return Results.Json(AccountContextView.From(context));
            });
            foreach (var route in new[] { "rename", "projects", "create" })
            {
                var operation = route;
                endpoints.MapPost("/api/v1/dashboard/organization/" + route,
                    async (HttpContext http, DashboardSessions sessions, AccountService accounts, IWorkspacePlacement placement) =>
                        await MutateAsync(http, sessions, accounts, placement, operation));
            }
            endpoints.MapPost("/api/v1/dashboard/organization/projects/{projectId}/rename",
                async (HttpContext http, DashboardSessions sessions, AccountService accounts, IWorkspacePlacement placement, string projectId) =>
                    await MutateAsync(http, sessions, accounts, placement, "rename-project", projectId));
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
            endpoints.MapPost("/api/v1/dashboard/organization/mfa-policy", async (HttpContext http, DashboardSessions sessions, AccountService accounts, ITsAuthMfa mfa, IWorkspacePlacement placement) =>
            {
                var context = await AccountRequest.RequireAsync(http, sessions, accounts);
                if (context is null)
                {
                    return Results.Unauthorized();
                }

                if (context.Role != "owner")
                {
                    return Results.StatusCode(403);
                }

                var form = await http.Request.ReadFormAsync(http.RequestAborted);
                if (!Csrf.Validate(http, form))
                {
                    return Results.BadRequest("Invalid form token.");
                }

                try
                {
                    var organization = context.Organization ?? throw new AccountAccessException("Select an organization.");
                    await mfa.SetRequireMfaAsync(context.Session, organization.Id, form["requireMfa"] == "true", http.RequestAborted);
                    return HttpRedirects.SeeOther("/organization");
                }
                catch (AccountAccessException error) { return await PageAsync(http, context, placement, error.Message, error.StatusCode, "organization"); }
            });
            endpoints.MapPost("/api/v1/dashboard/organization/leave", async (HttpContext http, DashboardSessions sessions, AccountService accounts, OrganizationLifecycleService lifecycle, IWorkspacePlacement placement) =>
                await LifecycleAsync(http, sessions, accounts, lifecycle, placement, "leave"));
            endpoints.MapPost("/api/v1/dashboard/organization/deletion", async (HttpContext http, DashboardSessions sessions, AccountService accounts, OrganizationLifecycleService lifecycle, IWorkspacePlacement placement) =>
                await LifecycleAsync(http, sessions, accounts, lifecycle, placement, "delete"));
            endpoints.MapPost("/api/v1/dashboard/organization/projects/{id}/delete", async (HttpContext http, DashboardSessions sessions, AccountService accounts, OrganizationLifecycleService lifecycle, IWorkspacePlacement placement, string id) =>
                await LifecycleAsync(http, sessions, accounts, lifecycle, placement, "delete-project", id));
            endpoints.MapOrganizationWizard();
            endpoints.MapInvitations();
            endpoints.MapOrganizationAudit();
            endpoints.MapOrganizationSso();
            endpoints.MapProjectSettings();
            Projects.ProjectInvitationEndpoints.MapProjectInvitations(endpoints);
            return endpoints;
        }

        private static IResult Notice(HttpContext http, AccountContext context, string tab, string message, int status)
        {
            var view = OrganizationView.Create(new(ShellModel.For(http, context, "Organization"), tab, [], Notice: message, NoticeStatus: status));
            view.StatusCode = status;
            return view;
        }

        internal static async Task<RazorSlice> PageAsync(HttpContext http, AccountContext context, IWorkspacePlacement placement, string error = "", int status = 200, string? tab = null)
        {
            var page = await PageModelAsync(http, context, placement, error, tab);
            var slice = OrganizationView.Create(page);
            slice.StatusCode = status;
            return slice;
        }

        private static async Task<OrganizationPage> PageModelAsync(HttpContext http, AccountContext context, IWorkspacePlacement placement, string error = "", string? tabOverride = null)
        {
            if (context.Organization is not null && context.Role.Length == 0)
            {
                throw new AccountAccessException("Organization membership is required.");
            }

            var tab = tabOverride ?? (http.Request.Query["newProject"] == "1" ? "projects" : http.Request.Query["tab"].ToString());
            if (tab is not ("projects" or "members" or "invitations" or "audit" or "sso" or "billing" or "danger"))
            {
                tab = "organization";
            }

            var regionsRead = tab == "projects" || http.Request.Query["newOrganization"] == "project" || http.Request.Query["newProject"] == "1"
                ? placement.RegionsAsync(http.RequestAborted) : null;
            // The projects and billing tabs render no members, invitations, audit events or ownership transfer.
            var accessRead = context.Organization is not null && tab is not ("projects" or "billing")
                ? http.RequestServices.GetRequiredService<IOrganizationAccess>().ReadAsync(context, http.RequestAborted) : null;
            IReadOnlyList<string> regions = regionsRead is null ? [] : await regionsRead;
            var access = accessRead is null ? null : await accessRead;
            var wizard = http.Request.Query["newOrganization"].ToString();
            if (wizard is not ("organization" or "payment" or "project"))
            {
                wizard = "";
            }

            if (wizard.Length > 0 && wizard != "organization")
            {
                if (context.Organization?.Kind != "business" || context.Role != "owner")
                {
                    wizard = "organization";
                }
                else if (!context.Organization.CanProvision(context.UserId))
                {
                    wizard = "payment";
                }
                else
                {
                    wizard = "project";
                }
            }
            if (error.Length == 0 && http.Request.Query["error"].ToString() is { Length: > 0 and <= 500 } queryError)
            {
                error = queryError;
            }
            // Only an owner sets the two-factor policy; the card stays hidden while ts-auth does not report it.
            var requireMfa = tab == "organization" && context.Role == "owner" && context.Organization is { } owned
                ? await http.RequestServices.GetRequiredService<ITsAuthMfa>().GetRequireMfaAsync(context.Session, owned.Id, http.RequestAborted) : null;
            return new(ShellModel.For(http, context, "Organization"), tab, regions, error,
                http.Request.Query["newProject"] == "1", wizard.Length > 0, access, Filters: OrganizationFilters.For(http), WizardStep: wizard, PaymentCanceled: http.Request.Query["payment"] == "canceled",
                Status: http.Request.Query["deleted"] == "1" ? "Organization deleted." : "", RequireMfa: requireMfa,
                Sso: tab == "sso" && context.CanManage ? await http.RequestServices.GetRequiredService<OrganizationSsoService>().ReadAsync(context, http.RequestAborted) : null);
        }

        private static async Task<IResult> AccessMutationAsync(HttpContext http, DashboardSessions sessions, AccountService accounts, IOrganizationAccess access, IWorkspacePlacement placement, string operation, string? id = null)
        {
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null)
            {
                return Results.Unauthorized();
            }

            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            if (!Csrf.Validate(http, form))
            {
                return Results.BadRequest("Invalid form token.");
            }

            try
            {
                if (operation is "invite" or "resend-invitation")
                {
                    var invitations = http.RequestServices.GetRequiredService<InvitationService>();
                    var created = operation == "invite" ? await invitations.CreateAsync(context, form["email"].ToString(), form["role"].ToString() is { Length: > 0 } role ? role : "member", http.RequestAborted)
                        : await invitations.ResendAsync(context, id!, http.RequestAborted);
                    context = await AccountScope.RefreshAsync(accounts, context, http.RequestAborted);
                    var view = await access.ReadAsync(context, http.RequestAborted);
                    http.Response.Headers.CacheControl = "no-store";
                    http.Response.Headers["Referrer-Policy"] = "strict-origin";
                    return OrganizationView.Create(new(ShellModel.For(http, context, "Organization"), http.Request.Query["tab"] == "invitations" ? "invitations" : "members", [], Access: view,
                        InvitationLink: $"{http.Request.Scheme}://{http.Request.Host}/invitations/{Uri.EscapeDataString(created.Token)}"));
                }
                if (operation == "revoke-invitation")
                {
                    await http.RequestServices.GetRequiredService<InvitationService>().RevokeAsync(context, id!, http.RequestAborted);
                }
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
                return HttpRedirects.SeeOther(operation.StartsWith("ownership", StringComparison.Ordinal) ? "/organization?tab=danger" : "/organization?tab=members");
            }
            catch (AccountAccessException error) { return await PageAsync(http, context, placement, error.Message, error.StatusCode, operation.StartsWith("ownership", StringComparison.Ordinal) ? "danger" : null); }
        }

        private static async Task<IResult> LifecycleAsync(HttpContext http, DashboardSessions sessions, AccountService accounts, OrganizationLifecycleService lifecycle, IWorkspacePlacement placement, string operation, string? projectId = null)
        {
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null)
            {
                return Results.Unauthorized();
            }

            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            if (!Csrf.Validate(http, form))
            {
                return Results.StatusCode(403);
            }

            try
            {
                if (operation == "leave")
                {
                    await lifecycle.LeaveAsync(context, http.RequestAborted);
                }
                else if (operation == "delete-project")
                {
                    await lifecycle.DeleteProjectAsync(context, projectId!, form["confirmation"].ToString(), http.RequestAborted);
                }
                else
                {
                    await lifecycle.DeleteAsync(context, form["confirmation"].ToString(), http.RequestAborted);
                }
                // Leaving or deleting moves the account to another organization.
                if (operation is "leave" or "delete" || operation == "delete-project" && context.Project?.Id == projectId)
                {
                    context = await accounts.GetContextAsync(context.Session, http.RequestAborted,
                        organizationId: operation is "leave" or "delete" ? null : context.Organization?.Id);
                }

                return HttpRedirects.SeeOther(WorkspaceUrl.For(context,
                    operation == "delete-project" ? "/organization?tab=projects" : operation == "delete" ? "/organization?deleted=1" : "/organization", replace: true));
            }
            catch (AccountAccessException error) when (error.Code == "deletion_pending")
            { return DeletionProgress.Create(http, form, http.Request.Path + "?organizationId=" + Uri.EscapeDataString(context.Organization!.Id)); }
            catch (Exception error) when (operation != "leave" && (error is HttpRequestException or IOException || error is OperationCanceledException && !http.RequestAborted.IsCancellationRequested))
            { return DeletionProgress.Create(http, form, http.Request.Path + "?organizationId=" + Uri.EscapeDataString(context.Organization!.Id)); }
            catch (AccountAccessException error) { return await PageAsync(http, context, placement, error.Message, error.StatusCode, operation == "delete-project" ? "projects" : "danger"); }
        }

        internal static async Task<IResult> MutateAsync(HttpContext http, DashboardSessions sessions, AccountService accounts, IWorkspacePlacement placement, string operation, string? projectId = null, AccountContext? scopedContext = null)
        {
            var context = scopedContext ?? await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null)
            {
                return Results.Unauthorized();
            }

            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            if (!Csrf.Validate(http, form))
            {
                return Results.BadRequest("Invalid form token.");
            }

            try
            {
                var name = AccountService.ValidName(form["name"].ToString());
                if (operation == "create")
                {
                    var organization = await accounts.CreateBusinessAsync(context.Session, name, http.RequestAborted);
                    var selected = await accounts.GetContextAsync(context.Session, http.RequestAborted, organizationId: organization.Id);
                    return HttpRedirects.SeeOther(WorkspaceUrl.For(selected, "/organization?tab=billing", replace: true));
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
                if (regions.Length == 0 && primary.Length > 0)
                {
                    regions = [primary];
                }

                if (regions.Length == 0)
                {
                    throw new AccountAccessException("Choose at least one serving region.", 422);
                }

                if (operation == "projects")
                {
                    await operations.CreateProjectAsync(context, name, primary, backup, regions, http.RequestAborted);
                }
                else
                {
                    var project = context.Organization?.Projects.GetValueOrDefault(projectId ?? "") ?? throw new AccountAccessException("Project is unavailable.", 404);
                    await operations.UpdateProjectAsync(context with { Project = project }, name, primary, backup, regions, http.RequestAborted);
                }
                return HttpRedirects.SeeOther(WorkspaceUrl.For(context, "/organization?tab=projects", replace: true));
            }
            catch (AccountAccessException error)
            {
                if (operation != "projects")
                {
                    return await PageAsync(http, context, placement, error.Message, error.StatusCode);
                }

                var available = await placement.RegionsAsync(http.RequestAborted);
                var page = (await PageModelAsync(http, context, placement, error.Message)) with
                {
                    Tab = "projects",
                    Regions = available,
                    OpenCreateProject = true,
                    ProjectDraftName = form["name"].ToString(),
                    ProjectDraftRegions = [.. form["regions"].Select(value => value ?? "").Where(available.Contains)],
                    ProjectDraftPrimaryRegion = form["region"].ToString(),
                    ProjectDraftBackupRegion = form["backupRegion"].ToString()
                };
                var slice = OrganizationView.Create(page);
                slice.StatusCode = error.StatusCode;
                return slice;
            }
        }
    }
}
