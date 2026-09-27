package home

import (
	"context"

	"terrascale.tech/dashboard/internal/core"
)

type overviewInput struct {
	core.Scope
	Range string `json:"range,omitempty" jsonschema:"Activity range: 24h, 7d, or 30d. Defaults to 7d."`
}

func mountTools(tools *core.Tools, service Service) {
	core.AddTool(tools, core.ToolSpec{
		Name: "get_overview", Title: "Get overview", ReadOnly: true,
		Description: "Summarize a project like the dashboard home page: database, member, notification, and credential counts, recent activity, and getting-started progress.",
	}, func(_ context.Context, p core.Principal, in overviewInput) (overview, error) {
		return service.Overview(p, in.Range)
	})
}
