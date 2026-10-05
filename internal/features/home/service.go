package home

import (
	"context"
	"errors"
	"strings"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/features/demovisuals"
)

var (
	errOrganizationUnavailable = errors.New("selected organization is unavailable")
	errProjectUnavailable      = errors.New("selected project is unavailable")
	// errFirstProjectNeeded marks a new business organization that must
	// create its first project before it has an overview.
	errFirstProjectNeeded = errors.New("create the organization's first project to see its overview")
)

// Service builds the account overview. HTTP handlers and MCP tools both
// call it.
type Service struct {
	deps core.Deps
}

type overview struct {
	OrganizationID      string   `json:"organizationId"`
	ProjectID           string   `json:"projectId"`
	ProjectName         string   `json:"projectName"`
	DatabaseCount       int      `json:"databaseCount"`
	OrganizationMembers int      `json:"organizationMembers" jsonschema:"Active organization members, including the owner."`
	UnreadNotifications int      `json:"unreadNotifications"`
	TotalNotifications  int      `json:"totalNotifications"`
	ActiveCredentials   int      `json:"activeCredentials" jsonschema:"Unrevoked project API keys and machine credentials."`
	Activity            activity `json:"activity"`
	GettingStarted      []task   `json:"gettingStarted"`
}

type activity struct {
	Range      string  `json:"range" jsonschema:"Plotted range: 24h, 7d, or 30d."`
	Sample     bool    `json:"sample" jsonschema:"True when the series is illustrative demo data rather than measured traffic."`
	Note       string  `json:"note"`
	TotalLabel string  `json:"totalLabel"`
	Total      int64   `json:"total"`
	Points     []int64 `json:"points" jsonschema:"Operations per interval, oldest first."`
}

type task struct {
	Title string `json:"title"`
	Done  bool   `json:"done"`
	Path  string `json:"path" jsonschema:"Dashboard page where the task is done."`
}

// Overview summarizes the principal's selected project for activityRange.
func (s Service) Overview(ctx context.Context, p core.Principal, activityRange string) (overview, error) {
	account, err := s.deps.Store.SnapshotAs(ctx, p)
	if err != nil {
		return overview{}, err
	}
	org, ok := account.Organizations[p.OrganizationID]
	if !ok {
		return overview{}, errOrganizationUnavailable
	}
	project, ok := org.Projects[p.ProjectID]
	if !ok {
		if p.ProjectID == "" && org.Kind == "business" && org.Status == "active" &&
			!org.Archived && !org.DeletionRequested && len(org.Projects) == 0 &&
			org.OwnerUserID == p.UserID &&
			(org.BillingStatus == "billing_required" || org.BillingStatus == "active") {
			return overview{}, errFirstProjectNeeded
		}
		return overview{}, errProjectUnavailable
	}
	result := overview{
		OrganizationID: org.ID, ProjectID: project.ID, ProjectName: project.Name,
		DatabaseCount: len(project.Databases), Activity: projectActivity(s.deps.Demo, account, org, activityRange),
	}
	for _, key := range project.APIKeys {
		if key.RevokedAt.IsZero() {
			result.ActiveCredentials++
		}
	}
	ownerIncluded := false
	for userID, member := range org.Members {
		if member.Status == "" || strings.EqualFold(member.Status, "active") {
			result.OrganizationMembers++
			if userID == org.OwnerUserID {
				ownerIncluded = true
			}
		}
	}
	if !ownerIncluded && org.OwnerUserID != "" {
		result.OrganizationMembers++
	}
	for _, notification := range account.Notifications {
		if notification.VisibleTo(p) {
			result.TotalNotifications++
			if notification.Unread() {
				result.UnreadNotifications++
			}
		}
	}
	result.GettingStarted = []task{
		{Title: "Create first database", Done: result.DatabaseCount > 0, Path: "/databases"},
		{Title: "Create an Application credential", Done: result.ActiveCredentials > 0, Path: "/api-keys"},
	}
	return result, nil
}

// projectActivity is the seeded sample workload for the demo account and a
// zero baseline everywhere else, since workload telemetry is unavailable.
func projectActivity(demo bool, account core.Account, org core.Organization, activityRange string) activity {
	result := activity{
		Range:      demovisuals.NormalizeRange(activityRange),
		Note:       "Illustrative zero baseline; timestamped workload telemetry is unavailable. Audit events are listed separately.",
		TotalLabel: "Plotted operations",
	}
	if demovisuals.IsSeededMarioAccount(demo, account, org) {
		result.Sample = true
		result.Points = demovisuals.Workload(result.Range)
		result.Note, result.TotalLabel = "Sample organization activity for this demo; not measured provider traffic.", "Sample operations"
	} else {
		count := 7
		switch result.Range {
		case "24h":
			count = 24
		case "30d":
			count = 30
		}
		result.Points = make([]int64, count)
	}
	result.Total = demovisuals.Sum(result.Points)
	return result
}
