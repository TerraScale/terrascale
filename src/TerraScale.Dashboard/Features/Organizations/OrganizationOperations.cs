using System.Data;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.Dashboard.Platform.TsAuth;

namespace TerraScale.Dashboard.Features.Organizations
{
    public sealed record ProjectCreationUnconfirmed(string Message);

    public sealed record ProjectCreationConflicted(string Message);

    public readonly union ProjectCreateResult(ProjectRecord, ProjectCreationUnconfirmed, ProjectCreationConflicted);

    public sealed class OrganizationOperations(AccountService accounts, ITsAuthPlatform platform,
        IWorkspacePlacement placement, IOrganizationAccess access, DashboardSessions sessions, ProjectReservations reservations,
        OrganizationStore organizations, TimeProvider time, ILogger<OrganizationOperations> logger)
    {
        public async Task RenameAsync(AccountContext context, string name, CancellationToken cancellationToken)
        {
            context = await FreshManagerAsync(context, cancellationToken);
            name = AccountService.ValidName(name);
            await platform.RenameOrganizationAsync(context.Session, Selected(context).Id, name, cancellationToken);
            await accounts.UpdateOrganizationAsync(context, org =>
            {
                Manage(org, context.UserId);
                org.AuditEvents.Add(Event(context, "organization.renamed", name, "organization"));
            }, cancellationToken);
        }

        public const string UnconfirmedProjectMessage = "Project creation could not be confirmed. Reload the project list before trying again.";
        private static readonly TimeSpan CreateBudget = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan ReservationMargin = TimeSpan.FromMinutes(1);

        public async Task<ProjectCreateResult> CreateProjectAsync(AccountContext context, string name,
            IReadOnlyList<string>? regions, CancellationToken cancellationToken)
        {
            context.DemandWrite();
            context = await FreshManagerAsync(context, cancellationToken);
            name = AccountService.ValidName(name);
            var chosen = await LocationsAsync(regions, cancellationToken);
            var (reserved, slot) = await ReserveSlotAsync(context, name, cancellationToken);
            if (slot is null)
            {
                return new ProjectCreationConflicted(DashboardStorageOutcomes.ConflictMessage);
            }

            var organizationId = Selected(reserved).Id;
            var remaining = slot.ExpiresAt - ReservationMargin - time.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                await reservations.ReleaseAsync(slot.ReservationId, CancellationToken.None);
                throw new TimeoutException("Project creation could not start before its reserved slot expired.");
            }

            var budget = remaining < CreateBudget ? remaining : CreateBudget;
            var creation = await platform.TryCreateProjectAsync(reserved.Session, organizationId, name, budget, cancellationToken);
            return creation switch
            {
                TsAuthPlatformProject created => await AdoptAsync(reserved, slot.ReservationId, created, name, chosen),
                ProjectCreationRefused refused => await RefusedAsync(slot.ReservationId, refused),
                ProjectCreationUnknown unknown => Unconfirmed(unknown.Failure),
            };
        }

        private async Task<(AccountContext Context, ProjectSlotReserved? Slot)> ReserveSlotAsync(AccountContext context, string name, CancellationToken cancellationToken)
        {
            var organizationId = Selected(context).Id;
            for (var attempt = 0; ; attempt++)
            {
                var version = await reservations.VersionAsync(organizationId, cancellationToken)
                    ?? throw new AccountAccessException("Organization is unavailable.", StatusCodes.Status404NotFound);
                var fresh = await AccountScope.RefreshAsync(accounts, context, cancellationToken, reload: true);
                var organization = Selected(fresh);
                Manage(organization, fresh.UserId);
                CanCreate(organization, fresh.UserId, name);
                var limit = ProjectLimit(organization);
                var outcome = await reservations.ReserveAsync(organizationId, version, organization.Projects.Keys, limit, cancellationToken);
                (ProjectSlotReserved? Slot, int? Full, string? Failure) picked = outcome switch
                {
                    ProjectSlotReserved slot => (slot, null, null),
                    ProjectSlotsFull full => (null, full.Limit, null),
                    ProjectInventoryChanged => (null, null, null),
                    ProjectReservationFailed failed => (null, null, failed.Failure),
                };
                var (reserved, fullLimit, failure) = picked;
                if (fullLimit is { } capped)
                {
                    throw ProjectLimitReached(capped);
                }

                if (failure is { } unavailable)
                {
                    throw new IOException("The project slot could not be reserved (" + unavailable + ").");
                }

                if (reserved is not null || attempt >= 4)
                {
                    return (fresh, reserved);
                }

                await DashboardDb.ConflictBackoffAsync(attempt, time, cancellationToken);
            }
        }

        private async Task<ProjectCreateResult> AdoptAsync(AccountContext context, string reservation, TsAuthPlatformProject created, string name, List<string> regions)
        {
            var project = new ProjectRecord
            {
                Id = created.Id,
                OrganizationId = created.OrganizationId,
                Name = created.Name,
                Slug = AccountService.Slug(created.Name),
                CreatedAt = created.CreatedAt,
                Regions = regions
            };
            var audit = Event(context, "project.created", name, "project") with { Ip = accounts.ClientIp, Source = context.AuditSource("dashboard") };
            for (var attempt = 0; ; attempt++)
            {
                var adoption = await organizations.TryAdoptProjectAsync(project.OrganizationId, project, audit, reservation, CancellationToken.None);
                var picked = Outcome(adoption, attempt);
                if (picked.Adopted)
                {
                    break;
                }

                if (picked.Failure is { } failure)
                {
                    return Unconfirmed(failure);
                }

                await DashboardDb.ConflictBackoffAsync(attempt, time, CancellationToken.None);
            }

            await accounts.UpdateAccountAsync(context.Session, account =>
            {
                if (account.ActiveOrganizationId == project.OrganizationId && account.ActiveProjectId.Length == 0)
                {
                    account.ActiveProjectId = project.Id;
                }
            }, CancellationToken.None);
            return project;
        }

        private static (bool Adopted, string? Failure) Outcome(ProjectAdoption adoption, int attempt) => adoption switch
        {
            ProjectAdopted => (true, null),
            ProjectAdoptionConflicted => (false, attempt >= 4 ? "the organization changed concurrently" : null),
            ProjectAdoptionUnconfirmed unconfirmed => (false, unconfirmed.Failure),
        };

        private async Task<ProjectCreateResult> RefusedAsync(string reservation, ProjectCreationRefused refused)
        {
            await reservations.ReleaseAsync(reservation, CancellationToken.None);
            throw new AccountAccessException(refused.Message, refused.StatusCode, refused.Code);
        }

        private ProjectCreationUnconfirmed Unconfirmed(string failure)
        {
            logger.ProjectCreationUnconfirmed(failure);
            return new ProjectCreationUnconfirmed(UnconfirmedProjectMessage);
        }

        public async Task UpdateProjectAsync(AccountContext context, string name,
            IReadOnlyList<string>? regions, CancellationToken cancellationToken)
        {
            context = await AccountScope.RefreshAsync(accounts, context, cancellationToken, requireProject: true);
            if (SelectedProject(context).Role != "admin" || !context.CanWriteProjectData)
            {
                throw new AccountAccessException("Project admin access is required to rename this project.");
            }

            var placementRequested = regions is not null;
            if (!context.CanManage && placementRequested)
            {
                throw new AccountAccessException("Organization management is required to change project placement. Submit only a project name.");
            }

            name = AccountService.ValidName(name);
            var project = SelectedProject(context);
            if (!placementRequested)
            {
                if (project.Name != name)
                {
                    await platform.RenameProjectAsync(context.Session, Selected(context).Id, project.Id, name, cancellationToken);
                }

                return;
            }
            Duplicate(Selected(context), name, project.Id);
            var chosen = await LocationsAsync(regions, cancellationToken);
            if (project.Name != name)
            {
                await platform.RenameProjectAsync(context.Session, Selected(context).Id, project.Id, name, cancellationToken);
            }

            await accounts.UpdateOrganizationAsync(context, org =>
            {
                Manage(org, context.UserId);
                Duplicate(org, name, project.Id);
                var current = org.Projects.GetValueOrDefault(project.Id) ?? throw new AccountAccessException("Project is unavailable.", 404);
                if (current.OrganizationId != org.Id)
                {
                    throw new AccountAccessException("Project is unavailable.", 404);
                }

                if (project.Name == name && current.Regions.SequenceEqual(chosen))
                {
                    return;
                }

                current.Name = name;
                current.Slug = AccountService.Slug(name);
                current.Regions = chosen;
                org.AuditEvents.Add(Event(context, "project.renamed", name, "project"));
            }, cancellationToken);
        }

        public async Task MutateMemberAsync(AccountContext context, string operation, string userId, string role,
            CancellationToken cancellationToken, string? stepUpProof = null)
        {
            context = operation is "confirm" or "cancel"
                ? await AccountScope.RefreshAsync(accounts, context, cancellationToken)
                : operation == "start" ? await FreshOwnerAsync(context, cancellationToken)
                : await FreshManagerAsync(context, cancellationToken);
            if (operation is "role" or "remove" or "start")
            {
                var snapshot = await access.ReadAsync(context, cancellationToken);
                var member = snapshot.Members.SingleOrDefault(item => item.UserId == userId);
                if (member is null || member.Status != "active" || userId == context.UserId || member.Role == "owner")
                {
                    throw new AccountAccessException("Choose another active member than yourself or the owner.", 422);
                }
            }
            if (operation == "role")
            {
                AvailableRole(role);
                await access.ChangeRoleAsync(context, userId, role, cancellationToken);
            }
            else if (operation == "remove")
            {
                await access.RemoveMemberAsync(context, userId, cancellationToken);
            }
            else
            {
                await access.TransferAsync(context, operation, userId, stepUpProof, cancellationToken);
            }
            context = await AccountScope.RefreshAsync(accounts, context, cancellationToken);
            await accounts.UpdateOrganizationAsync(context, org =>
            {
                org.AuditEvents.Add(Event(context, operation switch
                {
                    "role" => "member.role_changed",
                    "remove" => "member.removed",
                    "start" => "ownership.requested",
                    "confirm" => "ownership.transferred",
                    _ => "ownership.canceled"
                }, userId, "member"));
            }, cancellationToken);
            if (operation == "remove")
            {
                try { await sessions.RevokeUserAsync(userId, cancellationToken); }
                catch (Exception error) when (error is IOException or HttpRequestException or OperationCanceledException)
                { logger.RevokeDashboardSessionsAfterCommittedMember(userId, error.GetType().Name); }
            }
        }

        public static void AvailableRole(string role)
        {
            if (role is not ("viewer" or "member" or "admin"))
            {
                throw new AccountAccessException("Choose an available role.", 422);
            }
        }

        public async Task<AccountContext> FreshOwnerAsync(AccountContext context, CancellationToken cancellationToken, bool requireProject = false)
        {
            context = await AccountScope.RefreshAsync(accounts, context, cancellationToken, requireProject);
            Owner(Selected(context), context.UserId);
            return context;
        }

        private async Task<AccountContext> FreshManagerAsync(AccountContext context, CancellationToken cancellationToken, bool requireProject = false)
        {
            context = await AccountScope.RefreshAsync(accounts, context, cancellationToken, requireProject);
            Manage(Selected(context), context.UserId);
            return context;
        }

        private static OrganizationRecord Selected(AccountContext context) =>
            context.Organization ?? throw new AccountAccessException("Select an organization.");

        private static ProjectRecord SelectedProject(AccountContext context) =>
            context.Project ?? throw new AccountAccessException("Project is unavailable.", StatusCodes.Status404NotFound);

        internal static void Manage(OrganizationRecord org, string userId)
        {
            if (!org.CanManage(userId))
            {
                throw new AccountAccessException("Organization management is required.");
            }
        }

        internal static void Owner(OrganizationRecord org, string userId)
        {
            if (org.OwnerUserId != userId || org.RoleFor(userId) != "owner")
            {
                throw new AccountAccessException("Organization ownership is required.");
            }
        }

        private static void CanCreate(OrganizationRecord org, string userId, string name)
        {
            if (!org.CanProvision(userId))
            {
                throw new AccountAccessException("Active billing is required to create projects.", 409);
            }

            DemandProjectRoom(org);
            Duplicate(org, name);
        }

        public static int ProjectLimit(OrganizationRecord org)
        {
            return org.InternalBilling is not null
            ? PlanCatalog.ResourceLimits(org).Projects is > 0 and var limit ? limit : int.MaxValue
            : PlanCatalog.Plans.Any(plan => plan.Id == org.PlanId) ? PlanCatalog.ResourceLimits(org.PlanId).Projects
            : throw new AccountAccessException("The organization plan is unavailable.", 503);
        }

        internal static void DemandProjectRoom(OrganizationRecord org)
        {
            var limit = ProjectLimit(org);
            if (org.Projects.Count >= limit)
            {
                throw ProjectLimitReached(limit);
            }
        }

        private static AccountAccessException ProjectLimitReached(int limit) =>
            new($"Your organization has reached its plan's project limit ({limit} {(limit == 1 ? "project" : "projects")}). Upgrade the plan on the organization's Billing tab or delete a project.", 409);

        private static void Duplicate(OrganizationRecord org, string name, string? except = null)
        {
            var slug = AccountService.Slug(name);
            if (org.Projects.Values.Any(project => project.Id != except && project.Slug.Equals(slug, StringComparison.OrdinalIgnoreCase)))
            {
                throw new AccountAccessException("A project with that name already exists.", 409);
            }
        }

        private async Task<List<string>> LocationsAsync(IReadOnlyList<string>? requested, CancellationToken cancellationToken)
        {
            var catalog = await placement.RegionsAsync(cancellationToken);
            if (catalog.Count == 0)
            {
                throw new IOException("Serving regions could not be discovered.");
            }

            if (requested is not { Count: > 0 })
            {
                throw new AccountAccessException("Choose at least one serving region.", 422);
            }

            var regions = new List<string>(requested.Count);
            foreach (var value in requested)
            {
                var region = value.Trim().ToUpperInvariant();
                if (region.Length == 0 || regions.Contains(region))
                {
                    throw new AccountAccessException("Choose each serving region once.", 422);
                }

                if (!catalog.Contains(region))
                {
                    throw new AccountAccessException("Choose serving project regions.", 422);
                }

                regions.Add(region);
            }

            return regions;
        }

        private AuditRecord Event(AccountContext context, string action, string target, string type)
        {
            return new(AccountService.NewId(), context.Account.Email, action, target, type, time.GetUtcNow());
        }
    }
}
