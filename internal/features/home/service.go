package home

import (
	"context"
	"errors"
	"strings"
	"time"

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

// activity is the project's reads and writes per interval. Each read is one
// read and each write one write; they are never added together.
type activity struct {
	Range       string   `json:"range" jsonschema:"Plotted range: 24h, 7d, or 30d."`
	Sample      bool     `json:"sample" jsonschema:"True when the series is illustrative demo data rather than measured traffic."`
	Metered     bool     `json:"metered" jsonschema:"True when TerraBase measured the series."`
	Note        string   `json:"note"`
	Reads       []int64  `json:"reads" jsonschema:"Reads per UTC hour (24h, 7d) or day (30d), oldest first; empty when nothing is measured."`
	Writes      []int64  `json:"writes" jsonschema:"Writes per interval, aligned with reads."`
	Labels      []string `json:"labels" jsonschema:"Each interval's start, aligned with reads."`
	TotalReads  int64    `json:"totalReads"`
	TotalWrites int64    `json:"totalWrites"`
}

type task struct {
	Title string `json:"title"`
	Done  bool   `json:"done"`
	Path  string `json:"path" jsonschema:"Dashboard page where the task is done."`
}

// Overview summarizes the principal's selected project for activityRange.
// token is the user's ts-auth access token, with which TerraBase's metered
// reads and writes are read; without one the activity is not measured.
func (s Service) Overview(ctx context.Context, p core.Principal, token, activityRange string) (overview, error) {
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
		DatabaseCount: len(project.Databases), Activity: s.projectActivity(ctx, token, account, org, project, activityRange),
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

// projectActivity is TerraBase's metered reads and writes of the project,
// the seeded sample workload for the demo account, or nothing with the
// reason it is not measured.
func (s Service) projectActivity(ctx context.Context, token string, account core.Account, org core.Organization, project core.Project, activityRange string) activity {
	result := activity{Range: demovisuals.NormalizeRange(activityRange)}
	if demovisuals.IsSeededMarioAccount(s.deps.Demo, account, org) {
		result.Sample = true
		result.Reads, result.Writes, _ = demovisuals.Operations(demovisuals.Workload(result.Range))
		result.Labels = demovisuals.PointLabels(result.Range, len(result.Reads))
		result.Note = "Sample organization activity for this demo; not measured provider traffic."
	} else if s.deps.Usage == nil || token == "" {
		result.Note = "Reads and writes are metered by TerraBase for browser sessions signed in through TerraScale."
	} else {
		granularity, from := core.UsageBuckets(result.Range, time.Now())
		orgID, projectID := project.TerraBaseIDs(org.ID)
		usage, err := s.deps.Usage.Usage(ctx, token, core.UsageScope{OrganizationID: orgID, ProjectID: projectID, Granularity: granularity, From: from})
		switch {
		case errors.Is(err, core.ErrNotMetered):
			result.Note = "TerraBase doesn't report this project: it isn't stored there yet, or this account isn't a member of it there."
		case err != nil:
			result.Note = "Metered reads and writes could not be loaded. Try again shortly."
		default:
			result.Metered, result.Note = true, "Reads and writes metered by TerraBase."
			for _, point := range usage.Series {
				label := point.Start.UTC().Format("Jan 2, 15:04 UTC")
				if granularity == "day" {
					label = point.Start.UTC().Format("Jan 2")
				}
				result.Reads, result.Writes, result.Labels = append(result.Reads, point.Reads), append(result.Writes, point.Writes), append(result.Labels, label)
			}
		}
	}
	result.TotalReads, result.TotalWrites = demovisuals.Sum(result.Reads), demovisuals.Sum(result.Writes)
	return result
}
