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
            return endpoints;
        }

        internal static async Task<RazorSlice> PageAsync(HttpContext http, AccountContext context, IWorkspacePlacement placement, string error = "", int status = 200, string? tab = null)
        {
            var page = await PageModelAsync(http, context, placement, error, tab);
            var slice = OrganizationView.Create(page);
            slice.StatusCode = status;
            return slice;
        }

        internal static async Task<OrganizationPage> PageModelAsync(HttpContext http, AccountContext context, IWorkspacePlacement placement, string error = "", string? tabOverride = null)
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
                wizard = context.Organization?.Kind != "business" || context.Role != "owner"
                    ? "organization"
                    : !context.Organization.CanProvision(context.UserId) ? "payment" : "project";
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

        internal static async Task<IResult> AccessMutationAsync(HttpContext http, DashboardSessions sessions, AccountService accounts, IOrganizationAccess access, IWorkspacePlacement placement, string operation, string? id = null)
        {
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null)
            {
                return Results.Unauthorized();
            }

            var form = await FormRequests.ReadAsync(http, http.RequestAborted);
            if (!StepUpFlow.IsResuming(http) && !Csrf.Validate(http, form))
            {
                if (operation == "invite" && http.Request.Path.Equals("/api/v1/access/invitations", StringComparison.Ordinal))
                {
                    return await PageAsync(http, context, placement, "Invalid form token.", 400, "invitations");
                }

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
                    string? proof = null;
                    if (operation == "ownership")
                    {
                        if (context.Organization is null || context.Role != "owner")
                        {
                            throw new AccountAccessException("Organization ownership is required.");
                        }

                        var intent = StepUpFlow.Intent(http, context, "start_ownership_transfer");
                        if (intent is null)
                        {
                            return await StepUpFlow.StartAsync(http, context, "start_ownership_transfer", "ts-auth");
                        }

                        proof = intent.Challenge.Proof;
                    }

                    await operations.MutateMemberAsync(context, operation switch
                    {
                        "role" => "role",
                        "remove" => "remove",
                        "ownership" => "start",
                        "ownership/confirm" => "confirm",
                        _ => "cancel"
                    }, id ?? form["userID"].ToString(), form["role"].ToString(), http.RequestAborted, proof);
                }
                return HttpRedirects.SeeOther(operation.StartsWith("ownership", StringComparison.Ordinal) ? "/organization?tab=danger" : "/organization?tab=members");
            }
            catch (AccountAccessException error) { return await PageAsync(http, context, placement, error.Message, error.StatusCode, operation.StartsWith("ownership", StringComparison.Ordinal) ? "danger" : null); }
        }

        internal static async Task<IResult> LifecycleAsync(HttpContext http, DashboardSessions sessions, AccountService accounts, OrganizationLifecycleService lifecycle, IWorkspacePlacement placement, string operation, string? projectId = null)
        {
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null)
            {
                return Results.Unauthorized();
            }

            var form = await FormRequests.ReadAsync(http, http.RequestAborted);
            if (!StepUpFlow.IsResuming(http) && !Csrf.Validate(http, form))
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
                    if (context.Organization is null || context.Organization.Kind == "personal" || context.Role != "owner")
                    {
                        throw new AccountAccessException("Only the owner can delete a business organization.");
                    }

                    var intent = StepUpFlow.Intent(http, context, "delete_organization");
                    if (intent is null)
                    {
                        return await StepUpFlow.StartAsync(http, context, "delete_organization", "ts-auth");
                    }

                    await lifecycle.DeleteAsync(context, form["confirmation"].ToString(), http.RequestAborted, intent.Challenge.Proof);
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

            var form = await FormRequests.ReadAsync(http, http.RequestAborted);
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
