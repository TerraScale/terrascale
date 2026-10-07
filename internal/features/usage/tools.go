package usage

import (
	"context"
	"strings"

	"terrascale.tech/dashboard/internal/core"
)

type usageInput struct {
	core.Scope
	DatabaseID string `json:"databaseId,omitempty" jsonschema:"Report only this database of the project."`
	Range      string `json:"range,omitempty" jsonschema:"24h, 7d (the default), or 30d."`
}

type capChange struct {
	ID       string `json:"id" jsonschema:"Project or database ID in the organization."`
	CapCents int64  `json:"capCents" jsonschema:"Monthly spend cap in US cents; 0 removes the cap."`
}

type spendingControlsInput struct {
	core.Scope
	OrganizationCapCents *int64      `json:"organizationCapCents,omitempty" jsonschema:"Organization-wide monthly spend cap in US cents; 0 removes it. Omit to leave it unchanged."`
	PaidExcess           *bool       `json:"paidExcess,omitempty" jsonschema:"Bill usage above the plan allowance instead of blocking it. Omit to leave it unchanged."`
	ProjectCaps          []capChange `json:"projectCaps,omitempty" jsonschema:"Per-project caps to set; unlisted projects keep theirs."`
	DatabaseCaps         []capChange `json:"databaseCaps,omitempty" jsonschema:"Per-database caps to set; unlisted databases keep theirs."`
}

func mountTools(tools *core.Tools, service Service) {
	core.AddTool(tools, core.ToolSpec{
		Name: "get_usage", Title: "Get usage", ReadOnly: true,
		// The report lists every project of the organization, so it is an
		// organization route: a project token must not see its siblings.
		Route:       "GET /v1/organizations/{organizationId}/usage",
		RouteScope:  "usage:read",
		Description: "Report an organization's plan allowances and spending controls with the selected project's per-database storage, reads, and writes. TerraBase meters reads and writes for browser sessions signed in through TerraScale; tool calls report storage only.",
	}, func(ctx context.Context, p core.Principal, in usageInput) (usageReport, error) {
		// MCP access tokens carry no ts-auth token to read TerraBase with.
		return service.Usage(ctx, p, "", in.DatabaseID, in.Range)
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "set_spending_controls", Title: "Set spending controls", Idempotent: true,
		Route:       "PUT /v1/organizations/{organizationId}/spending-controls",
		RouteScope:  "billing:write",
		Description: "Set monthly spend caps for the organization, its projects, and its databases, and whether usage above the plan allowance is billed. Requires an owner or admin role.",
	}, func(ctx context.Context, p core.Principal, in spendingControlsInput) (usageReport, error) {
		controls := spendingControls{
			OrganizationCapCents: in.OrganizationCapCents, PaidExcess: in.PaidExcess,
			ProjectCaps: make(map[string]int64, len(in.ProjectCaps)), DatabaseCaps: make(map[string]int64, len(in.DatabaseCaps)),
		}
		addCaps := func(target map[string]int64, caps []capChange, kind string) error {
			for _, cap := range caps {
				if strings.TrimSpace(cap.ID) == "" {
					return core.Invalid(kind + " id is required")
				}
				if _, duplicate := target[cap.ID]; duplicate {
					return core.Invalid("duplicate " + kind + " cap for " + cap.ID)
				}
				target[cap.ID] = cap.CapCents
			}
			return nil
		}
		if err := addCaps(controls.ProjectCaps, in.ProjectCaps, "project"); err != nil {
			return usageReport{}, err
		}
		if err := addCaps(controls.DatabaseCaps, in.DatabaseCaps, "database"); err != nil {
			return usageReport{}, err
		}
		if err := service.SetSpendingControls(ctx, p, controls); err != nil {
			return usageReport{}, err
		}
		return service.Usage(ctx, p, "", "", "")
	})
}
