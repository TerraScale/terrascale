package audit

import (
	"context"
	"strconv"
	"time"

	"terrascale.tech/dashboard/internal/core"
)

const maxToolPageSize = 100

type auditEventView struct {
	ID          string    `json:"id"`
	OccurredAt  time.Time `json:"occurredAt"`
	Actor       string    `json:"actor"`
	ActorType   string    `json:"actorType" jsonschema:"user or service."`
	Action      string    `json:"action" jsonschema:"Raw action code, such as api_key.created."`
	ActionLabel string    `json:"actionLabel"`
	Target      string    `json:"target"`
	TargetType  string    `json:"targetType,omitempty"`
	Source      string    `json:"source"`
	Severity    string    `json:"severity" jsonschema:"info, warn, or critical."`
	Outcome     string    `json:"outcome" jsonschema:"success or failure."`
}

type auditEventsInput struct {
	core.Scope
	Search   string `json:"search,omitempty" jsonschema:"Case-insensitive text matched against actor, action, target, source, and severity."`
	Action   string `json:"action,omitempty" jsonschema:"Only events with this raw action code. Omit or use all for every action."`
	Severity string `json:"severity,omitempty" jsonschema:"info, warn, critical, or all (default)."`
	Range    string `json:"range,omitempty" jsonschema:"Time window: 24h, 7d (default), 30d, or 90d, capped by the organization's retention."`
	Page     int    `json:"page,omitempty" jsonschema:"1-based page number. Defaults to 1."`
	PageSize int    `json:"pageSize,omitempty" jsonschema:"Events per page, 1-100. Defaults to 10."`
}

type auditEventsOutput struct {
	Events        []auditEventView `json:"events"`
	Matching      int              `json:"matching" jsonschema:"Events matching the filters across all pages."`
	Critical      int              `json:"critical"`
	Warn          int              `json:"warn"`
	Info          int              `json:"info"`
	Page          int              `json:"page"`
	PageCount     int              `json:"pageCount"`
	Range         string           `json:"range"`
	Severity      string           `json:"severity"`
	Actions       []string         `json:"actions" jsonschema:"Action codes present in the retained history, for the action filter."`
	RetentionDays int              `json:"retentionDays"`
}

func mountTools(tools *core.Tools, service Service) {
	core.AddTool(tools, core.ToolSpec{
		Name: "list_audit_events", Title: "List audit events", ReadOnly: true,
		Enums: map[string][]string{
			"severity": {"info", "warn", "critical", "all"},
			"range":    {"24h", "7d", "30d", "90d"},
		},
		Description: "List the audit history of a project, newest first, with the same filters and pagination as the audit page and export.",
	}, func(_ context.Context, p core.Principal, in auditEventsInput) (auditEventsOutput, error) {
		size := pageSize
		if in.PageSize != 0 {
			if in.PageSize < 1 || in.PageSize > maxToolPageSize {
				return auditEventsOutput{}, core.Invalid("pageSize must be between 1 and 100")
			}
			size = in.PageSize
		}
		data, err := service.Page(p, in.Search, in.Action, in.Severity, in.Range, strconv.Itoa(in.Page), size)
		if err != nil {
			return auditEventsOutput{}, err
		}
		out := auditEventsOutput{
			Events: make([]auditEventView, 0, len(data.Rows)), Matching: data.Filtered,
			Critical: data.Critical, Warn: data.Warn, Info: data.Info, Page: data.Page, PageCount: data.PageCount,
			Range: data.Range, Severity: data.Severity, Actions: data.ActionOptions[1:], RetentionDays: data.Retention,
		}
		for _, item := range data.Rows {
			event := item.Event
			out.Events = append(out.Events, auditEventView{
				ID: event.ID, OccurredAt: event.OccurredAt, Actor: event.Actor, ActorType: item.ActorType,
				Action: event.Action, ActionLabel: item.ActionLabel, Target: event.Target, TargetType: event.TargetType,
				Source: event.Source, Severity: item.Severity, Outcome: item.Outcome,
			})
		}
		return out, nil
	})
}
