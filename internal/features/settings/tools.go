package settings

import (
	"context"

	"terrascale.tech/dashboard/internal/core"
)

type auditRetentionInput struct {
	core.Scope
	Days int `json:"days" jsonschema:"Days to keep the organization's audit events, from 1 to 90."`
}

type authenticationInput struct {
	core.Scope
	Enabled bool `json:"enabled" jsonschema:"Whether the project's authentication integration is on."`
}

type deleteProjectInput struct {
	core.Scope
	ConfirmName string `json:"confirmName" jsonschema:"The project's exact name, confirming permanent deletion."`
}

type saved struct {
	Saved bool `json:"saved"`
}

type deleted struct {
	Deleted bool `json:"deleted"`
}

func mountTools(tools *core.Tools, service Service) {
	core.AddTool(tools, core.ToolSpec{
		Name: "get_project_settings", Title: "Get project settings", ReadOnly: true,
		Description: "Show a project's authentication integration, identity providers, credential counts, per-database backup policies, audit retention, and whether it can be deleted.",
	}, func(ctx context.Context, p core.Principal, _ core.Scope) (projectSettings, error) {
		return service.Settings(ctx, p)
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "set_audit_retention", Title: "Set audit retention", Idempotent: true,
		Description: "Set how many days the organization keeps audit events (1-90). Requires an owner or admin role.",
	}, func(ctx context.Context, p core.Principal, in auditRetentionInput) (saved, error) {
		err := service.SetAuditRetention(ctx, p, in.Days)
		return saved{Saved: err == nil}, err
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "set_project_authentication", Title: "Set project authentication", Idempotent: true,
		Description: "Turn a project's authentication integration on or off. Requires an owner or admin role.",
	}, func(ctx context.Context, p core.Principal, in authenticationInput) (saved, error) {
		err := service.SetAuthentication(ctx, p, in.Enabled)
		return saved{Saved: err == nil}, err
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "delete_project", Title: "Delete project", Destructive: true,
		Description: "Permanently delete a project with its databases and credentials. The organization's default or last project cannot be deleted. Requires confirmName to equal the project name.",
	}, func(ctx context.Context, p core.Principal, in deleteProjectInput) (deleted, error) {
		err := service.DeleteProject(ctx, p, in.ConfirmName)
		return deleted{Deleted: err == nil}, err
	})
}
