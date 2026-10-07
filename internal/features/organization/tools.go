package organization

import (
	"context"
	"log/slog"
	"time"

	"terrascale.tech/dashboard/internal/core"
)

type userView struct {
	ID            string `json:"id"`
	Name          string `json:"name"`
	Email         string `json:"email"`
	PlatformAdmin bool   `json:"platformAdmin" jsonschema:"Whether the user can call the admin_ tools."`
}

type selectionView struct {
	OrganizationID string `json:"organizationId"`
	ProjectID      string `json:"projectId"`
}

type projectSummary struct {
	ID           string   `json:"id"`
	Name         string   `json:"name"`
	Region       string   `json:"region" jsonschema:"Primary region code."`
	BackupRegion string   `json:"backupRegion"`
	Regions      []string `json:"regions" jsonschema:"Every region the project's databases may be placed in."`
	IsDefault    bool     `json:"isDefault"`
	Databases    int      `json:"databases"`
}

type organizationSummary struct {
	ID                string           `json:"id"`
	Name              string           `json:"name"`
	Kind              string           `json:"kind" jsonschema:"personal or business."`
	Status            string           `json:"status"`
	Role              string           `json:"role" jsonschema:"The caller's role: owner, member, or viewer."`
	PlanID            string           `json:"planId"`
	PlanName          string           `json:"planName"`
	BillingStatus     string           `json:"billingStatus" jsonschema:"active, or billing_required until a business organization completes checkout."`
	CanManage         bool             `json:"canManage" jsonschema:"Whether the caller owns the organization and may change it."`
	CanProvision      bool             `json:"canProvision" jsonschema:"Whether the caller may create projects in the organization."`
	DeletionRequested bool             `json:"deletionRequested"`
	Projects          []projectSummary `json:"projects"`
}

type contextView struct {
	User          userView              `json:"user"`
	Selection     selectionView         `json:"selection" jsonschema:"The saved dashboard selection that tool calls use when they omit organizationId and projectId."`
	Organizations []organizationSummary `json:"organizations"`
}

type organizationDetail struct {
	organizationSummary
	Slug                  string     `json:"slug"`
	CreatedAt             time.Time  `json:"createdAt"`
	MemberCount           int        `json:"memberCount"`
	PendingInvitations    int        `json:"pendingInvitations"`
	CanLeave              bool       `json:"canLeave"`
	PendingOwnerUserID    string     `json:"pendingOwnerUserId,omitempty" jsonschema:"Member nominated to become owner, if a transfer is pending."`
	PendingOwnerExpiresAt *time.Time `json:"pendingOwnerExpiresAt,omitempty"`
	CanConfirmOwnership   bool       `json:"canConfirmOwnership" jsonschema:"Whether the caller is the nominated owner and may confirm the transfer."`
}

type projectResult struct {
	ID             string   `json:"id"`
	OrganizationID string   `json:"organizationId"`
	Name           string   `json:"name"`
	Region         string   `json:"region"`
	BackupRegion   string   `json:"backupRegion"`
	Regions        []string `json:"regions"`
}

type noInput struct{}

type nameInput struct {
	Name string `json:"name" jsonschema:"New name: 1-128 characters including a letter or digit."`
}

type scopedNameInput struct {
	core.Scope
	nameInput
}

type projectInput struct {
	core.Scope
	Name         string   `json:"name" jsonschema:"Project name: 1-128 characters including a letter or digit."`
	Region       string   `json:"region,omitempty" jsonschema:"Primary region code: GRU or SFO. Defaults to the first listed region."`
	BackupRegion string   `json:"backupRegion,omitempty" jsonschema:"Backup region code. Defaults to the first listed region."`
	Regions      []string `json:"regions,omitempty" jsonschema:"Region codes the project may use. Defaults to the primary region."`
}

type selectOrganizationInput struct {
	OrganizationID string `json:"organizationId" jsonschema:"Organization to select in the dashboard."`
}

type selectProjectInput struct {
	ProjectID string `json:"projectId" jsonschema:"Project to select in the dashboard, together with its organization."`
}

type memberInput struct {
	core.Scope
	UserID string `json:"userId" jsonschema:"Member user ID from list_members."`
}

type memberRoleInput struct {
	memberInput
	Role string `json:"role" jsonschema:"member (read and write data) or viewer (read only)."`
}

type deletionInput struct {
	core.Scope
	ConfirmName string `json:"confirmName" jsonschema:"The organization's exact name, confirming the deletion request."`
}

type done struct {
	OK bool `json:"ok"`
}

func mountTools(tools *core.Tools, service Service) {
	core.AddTool(tools, core.ToolSpec{
		Name: "get_context", Title: "Get context", ReadOnly: true,
		Description: "Show the signed-in user, the saved dashboard selection, and every organization and project the user can access with their IDs. Call this first to find the organizationId and projectId other tools accept.",
	}, func(ctx context.Context, p core.Principal, _ noInput) (contextView, error) {
		return service.Context(ctx, p)
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "get_organization", Title: "Get organization", ReadOnly: true,
		Description: "Show an organization's plan, billing status, projects, member and invitation counts, and pending ownership transfer.",
	}, func(ctx context.Context, p core.Principal, _ core.Scope) (organizationDetail, error) {
		return service.Overview(ctx, p)
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "create_organization", Title: "Create organization",
		Description: "Create a business organization owned by the caller and select it. It starts with billing_required; complete checkout with the billing tools before creating projects.",
	}, func(ctx context.Context, p core.Principal, in nameInput) (organizationSummary, error) {
		org, err := service.CreateBusiness(ctx, p, in.Name)
		if err != nil {
			return organizationSummary{}, err
		}
		summary, err := service.summary(ctx, org, p.UserID)
		if err != nil {
			// The organization is created; describe it without its plan
			// name rather than report the creation as failed.
			slog.Warn("describe created organization", "organization_id", org.ID, "error", err)
			summary = describeOrganization(org, p.UserID)
		}
		return summary, nil
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "rename_organization", Title: "Rename organization", Idempotent: true,
		Description: "Rename an organization. Requires the owner role.",
	}, func(ctx context.Context, p core.Principal, in scopedNameInput) (done, error) {
		return done{OK: true}, service.RenameOrganization(ctx, p, in.Name)
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "select_organization", Title: "Select organization", Idempotent: true,
		Description: "Save an organization and its default project as the dashboard selection, which tools use when a call omits organizationId and projectId.",
	}, func(ctx context.Context, p core.Principal, in selectOrganizationInput) (selectionView, error) {
		if err := service.SwitchOrganization(ctx, p, in.OrganizationID); err != nil {
			return selectionView{}, err
		}
		return service.selection(ctx, p)
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "select_project", Title: "Select project", Idempotent: true,
		Description: "Save a project and its organization as the dashboard selection, which tools use when a call omits organizationId and projectId.",
	}, func(ctx context.Context, p core.Principal, in selectProjectInput) (selectionView, error) {
		if err := service.SwitchProject(ctx, p, in.ProjectID); err != nil {
			return selectionView{}, err
		}
		return service.selection(ctx, p)
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "create_project", Title: "Create project",
		Description: "Create a project in an organization. Requires the owner role and, for business organizations, active billing.",
	}, func(ctx context.Context, p core.Principal, in projectInput) (projectResult, error) {
		project, err := service.CreateProject(ctx, p, in.Name, in.Region, in.BackupRegion, in.Regions)
		return projectResultFrom(project), err
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "update_project", Title: "Update project", Idempotent: true,
		Description: "Rename the project projectId selects and optionally change its regions. Requires the owner role.",
	}, func(ctx context.Context, p core.Principal, in projectInput) (done, error) {
		return done{OK: true}, service.RenameProject(ctx, p, p.ProjectID, in.Name, in.Region, in.BackupRegion, in.Regions)
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "update_member_role", Title: "Update member role", Idempotent: true,
		Description: "Change a member's role to member or viewer. Requires the owner role.",
	}, func(ctx context.Context, p core.Principal, in memberRoleInput) (done, error) {
		return done{OK: true}, service.UpdateMemberRole(ctx, p, in.UserID, in.Role)
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "remove_member", Title: "Remove member", Destructive: true,
		Description: "Remove a member from an organization and sign them out. Requires the owner role.",
	}, func(ctx context.Context, p core.Principal, in memberInput) (done, error) {
		return done{OK: true}, service.RemoveMember(ctx, p, in.UserID)
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "request_ownership_transfer", Title: "Request ownership transfer", Idempotent: true,
		Description: "Nominate an active member as the next owner. The member must confirm with confirm_ownership_transfer within 7 days. Requires the owner role.",
	}, func(ctx context.Context, p core.Principal, in memberInput) (done, error) {
		return done{OK: true}, service.RequestOwnershipTransfer(ctx, p, in.UserID)
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "confirm_ownership_transfer", Title: "Confirm ownership transfer", Destructive: true,
		Description: "Accept a pending ownership transfer nominated to the caller. The previous owner becomes a member.",
	}, func(ctx context.Context, p core.Principal, _ core.Scope) (done, error) {
		return done{OK: true}, service.ConfirmOwnershipTransfer(ctx, p)
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "request_organization_deletion", Title: "Request organization deletion", Destructive: true, Idempotent: true,
		Description: "Request deletion of a business organization. Requires the owner role and confirmName equal to the organization name.",
	}, func(ctx context.Context, p core.Principal, in deletionInput) (done, error) {
		return done{OK: true}, service.RequestDeletion(ctx, p, in.ConfirmName)
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "leave_organization", Title: "Leave organization", Destructive: true,
		Description: "Leave a business organization the caller does not own. The dashboard selection moves to another organization.",
	}, func(ctx context.Context, p core.Principal, _ core.Scope) (done, error) {
		return done{OK: true}, service.LeaveOrganization(ctx, p)
	})
}

func (s Service) selection(ctx context.Context, p core.Principal) (selectionView, error) {
	a, err := s.store.SnapshotAs(ctx, core.Principal{UserID: p.UserID, AccountID: p.AccountID, AuthVersion: p.AuthVersion})
	if err != nil {
		return selectionView{}, err
	}
	return selectionView{OrganizationID: a.ActiveOrganizationID, ProjectID: a.ActiveProjectID}, nil
}

func projectResultFrom(project core.Project) projectResult {
	return projectResult{
		ID: project.ID, OrganizationID: project.OrganizationID, Name: project.Name,
		Region: project.Region, BackupRegion: project.BackupRegion, Regions: append([]string{}, project.Regions...),
	}
}
