package admin

import (
	"context"
	"time"

	"terrascale.tech/dashboard/internal/core"
)

type searchInput struct {
	Query string `json:"query,omitempty" jsonschema:"Case-insensitive text filter over the listed fields, up to 200 characters."`
}

type usersOutput struct {
	Users []userRow `json:"users"`
}

type organizationsOutput struct {
	Organizations []organizationRow `json:"organizations"`
}

type projectsOutput struct {
	Projects []projectRow `json:"projects"`
}

type databasesOutput struct {
	Databases []databaseRow `json:"databases"`
}

type estateAction struct {
	ActorUserID string    `json:"actorUserId"`
	Action      string    `json:"action"`
	Target      string    `json:"target"`
	Reason      string    `json:"reason"`
	RecordedAt  time.Time `json:"recordedAt"`
}

type estateOutput struct {
	Regions   []estateRow     `json:"regions" jsonschema:"Regions with the projects and databases observed in them."`
	Databases []databaseRow   `json:"databases"`
	Actions   []estateAction  `json:"actions" jsonschema:"Recorded estate actions, newest first."`
	Inventory inventoryCounts `json:"inventory"`
}

type usageOutput struct {
	Inventory inventoryCounts `json:"inventory" jsonschema:"Totals of the records in the dashboard store. Live request and storage metrics are not available here."`
}

type planView struct {
	ID                 string `json:"id"`
	Key                string `json:"key"`
	Name               string `json:"name"`
	PriceCents         int64  `json:"priceCents" jsonschema:"Monthly price in US cents."`
	AnnualPriceCents   int64  `json:"annualPriceCents" jsonschema:"Annual price in US cents."`
	TrialDays          int    `json:"trialDays"`
	Visible            bool   `json:"visible" jsonschema:"Whether customers can choose the plan."`
	StorageMiB         int64  `json:"storageMiB"`
	MonthlyRequests    int64  `json:"monthlyRequests"`
	OperationsPerMonth int64  `json:"operationsPerMonth"`
	StorageLabel       string `json:"storageLabel,omitempty"`
	MaxDatabases       int    `json:"maxDatabases" jsonschema:"Database cap; 0 means unlimited."`
	MaxMembers         int    `json:"maxMembers" jsonschema:"Member cap; 0 means unlimited."`
}

type plansOutput struct {
	Plans []planView `json:"plans"`
}

type updateUserInput struct {
	AccountID     string `json:"accountId" jsonschema:"Account ID from admin_list_users."`
	Name          string `json:"name" jsonschema:"Display name, 1-128 characters."`
	PlatformAdmin bool   `json:"platformAdmin" jsonschema:"Whether the user is a platform administrator. The last administrator cannot be demoted."`
}

type renameOrganizationInput struct {
	AccountID      string `json:"accountId" jsonschema:"Owning account ID from admin_list_organizations."`
	OrganizationID string `json:"organizationId"`
	Name           string `json:"name" jsonschema:"New organization name, 1-128 characters."`
}

type renameProjectInput struct {
	AccountID      string `json:"accountId" jsonschema:"Owning account ID from admin_list_projects."`
	OrganizationID string `json:"organizationId"`
	ProjectID      string `json:"projectId"`
	Name           string `json:"name" jsonschema:"New project name, 1-128 characters."`
}

type estateActionInput struct {
	Action   string `json:"action" jsonschema:"One of pause, resume, guard, spendCap, trial, refund, reaperExemption, flag, or viewAs."`
	TargetID string `json:"targetId" jsonschema:"ID of the region, host, database, or tenant the action concerns."`
	Reason   string `json:"reason" jsonschema:"Why the action is taken, 3-1000 characters."`
}

type savePlanInput struct {
	PlanID           string `json:"planId,omitempty" jsonschema:"Plan to update. Omit to create a new plan."`
	Key              string `json:"key" jsonschema:"Stable plan key: lowercase letters, digits, hyphens, and underscores. Existing plans keep their key."`
	Name             string `json:"name"`
	PriceCents       int64  `json:"priceCents" jsonschema:"Monthly price in US cents."`
	AnnualPriceCents int64  `json:"annualPriceCents" jsonschema:"Annual price in US cents."`
	TrialDays        int    `json:"trialDays"`
	Visible          bool   `json:"visible" jsonschema:"Whether customers can choose the plan."`
	StorageMiB       int64  `json:"storageMiB"`
	MonthlyRequests  int64  `json:"monthlyRequests"`
}

type planVisibilityInput struct {
	PlanID  string `json:"planId"`
	Visible bool   `json:"visible" jsonschema:"Whether customers can choose the plan."`
}

type planInput struct {
	PlanID string `json:"planId"`
}

type done struct {
	OK bool `json:"ok"`
}

func mountTools(tools *core.Tools, service Service) {
	core.AddTool(tools, adminRead("admin_list_users", "List users", "List every dashboard account with its organization and project counts."),
		func(_ context.Context, p core.Principal, in searchInput) (usersOutput, error) {
			data, err := service.page(p, "users", in.Query)
			return usersOutput{Users: nonNil(data.Users)}, err
		})
	core.AddTool(tools, adminRead("admin_list_organizations", "List organizations", "List every organization with its owner, status, and project count."),
		func(_ context.Context, p core.Principal, in searchInput) (organizationsOutput, error) {
			data, err := service.page(p, "organizations", in.Query)
			return organizationsOutput{Organizations: nonNil(data.Organizations)}, err
		})
	core.AddTool(tools, adminRead("admin_list_projects", "List projects", "List every project with its organization, region, status, and database count."),
		func(_ context.Context, p core.Principal, in searchInput) (projectsOutput, error) {
			data, err := service.page(p, "projects", in.Query)
			return projectsOutput{Projects: nonNil(data.Projects)}, err
		})
	core.AddTool(tools, adminRead("admin_list_databases", "List databases", "List every database across all tenants with its project, region, and status."),
		func(_ context.Context, p core.Principal, in searchInput) (databasesOutput, error) {
			data, err := service.page(p, "databases", in.Query)
			return databasesOutput{Databases: nonNil(data.Databases)}, err
		})
	core.AddTool(tools, adminRead("admin_get_estate", "Get estate", "Show the platform estate: regions with observed projects and databases, every database, and the recorded estate actions."),
		func(_ context.Context, p core.Principal, in searchInput) (estateOutput, error) {
			data, err := service.page(p, "estate", in.Query)
			if err != nil {
				return estateOutput{}, err
			}
			out := estateOutput{
				Regions: nonNil(data.Regions), Databases: nonNil(data.EstateDatabases),
				Actions: make([]estateAction, 0, len(data.EstateActions)), Inventory: data.Inventory,
			}
			for _, action := range data.EstateActions {
				out.Actions = append(out.Actions, estateAction{
					ActorUserID: action.ActorUserID, Action: action.Action, Target: action.Target,
					Reason: action.Reason, RecordedAt: action.RecordedAt,
				})
			}
			return out, nil
		})
	core.AddTool(tools, adminRead("admin_get_usage", "Get platform usage", "Show platform-wide totals of accounts, organizations, projects, and databases."),
		func(_ context.Context, p core.Principal, _ struct{}) (usageOutput, error) {
			data, err := service.page(p, "usage", "")
			return usageOutput{Inventory: data.Inventory}, err
		})
	core.AddTool(tools, adminRead("admin_list_plans", "List plans", "List every pricing plan, including hidden plans, with prices and limits."),
		func(_ context.Context, p core.Principal, in searchInput) (plansOutput, error) {
			data, err := service.page(p, "plans", in.Query)
			out := plansOutput{Plans: make([]planView, 0, len(data.Plans))}
			for _, plan := range data.Plans {
				out.Plans = append(out.Plans, viewPlan(plan))
			}
			return out, err
		})

	core.AddTool(tools, core.ToolSpec{
		Name: "admin_update_user", Title: "Update user", Admin: true, Idempotent: true,
		Description: "Change a user's display name and platform administrator flag.",
	}, func(_ context.Context, p core.Principal, in updateUserInput) (done, error) {
		return done{OK: true}, service.UpdateUser(p, in.AccountID, in.Name, in.PlatformAdmin)
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "admin_rename_organization", Title: "Rename organization", Admin: true, Idempotent: true,
		Description: "Rename any tenant's organization.",
	}, func(_ context.Context, p core.Principal, in renameOrganizationInput) (done, error) {
		return done{OK: true}, service.RenameOrganization(p, in.AccountID, in.OrganizationID, in.Name)
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "admin_rename_project", Title: "Rename project", Admin: true, Idempotent: true,
		Description: "Rename any tenant's project.",
	}, func(_ context.Context, p core.Principal, in renameProjectInput) (done, error) {
		return done{OK: true}, service.RenameProject(p, in.AccountID, in.OrganizationID, in.ProjectID, in.Name)
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "admin_record_estate_action", Title: "Record estate action", Admin: true,
		Description: "Record an estate operation with its reason in the platform audit log. No infrastructure change is made.",
	}, func(_ context.Context, p core.Principal, in estateActionInput) (done, error) {
		return done{OK: true}, service.RecordEstateAction(p, in.Action, in.TargetID, in.Reason)
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "admin_save_plan", Title: "Save plan", Admin: true,
		Description: "Create a pricing plan, or update an existing plan's name, prices, trial, visibility, storage, and request limits.",
	}, func(_ context.Context, p core.Principal, in savePlanInput) (planView, error) {
		plan, err := service.SavePlan(p, core.Plan{
			ID: in.PlanID, Key: in.Key, Name: in.Name, PriceCents: in.PriceCents, AnnualPriceCents: in.AnnualPriceCents,
			TrialDays: in.TrialDays, Visible: in.Visible, StorageMiB: in.StorageMiB, MonthlyRequests: in.MonthlyRequests,
		})
		return viewPlan(plan), err
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "admin_set_plan_visibility", Title: "Set plan visibility", Admin: true, Idempotent: true,
		Description: "Show or hide a pricing plan for customers.",
	}, func(_ context.Context, p core.Principal, in planVisibilityInput) (done, error) {
		return done{OK: true}, service.SetPlanVisibility(p, in.PlanID, in.Visible)
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "admin_delete_plan", Title: "Delete plan", Admin: true, Destructive: true,
		Description: "Delete a pricing plan that no organization or open checkout uses.",
	}, func(_ context.Context, p core.Principal, in planInput) (done, error) {
		return done{OK: true}, service.DeletePlan(p, in.PlanID)
	})
}

func adminRead(name, title, description string) core.ToolSpec {
	return core.ToolSpec{Name: name, Title: title, Description: description, ReadOnly: true, Admin: true}
}

func viewPlan(plan core.Plan) planView {
	return planView{
		ID: plan.ID, Key: plan.Key, Name: plan.Name, PriceCents: plan.PriceCents, AnnualPriceCents: plan.AnnualPriceCents,
		TrialDays: plan.TrialDays, Visible: plan.Visible, StorageMiB: plan.StorageMiB, MonthlyRequests: plan.MonthlyRequests,
		OperationsPerMonth: plan.OperationsPerMonth, StorageLabel: plan.StorageLabel,
		MaxDatabases: plan.MaxDatabases, MaxMembers: plan.MaxMembers,
	}
}

// nonNil keeps empty inventories as JSON arrays rather than null.
func nonNil[T any](rows []T) []T {
	if rows == nil {
		return []T{}
	}
	return rows
}
