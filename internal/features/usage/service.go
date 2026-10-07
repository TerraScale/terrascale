package usage

import (
	"context"
	"errors"
	"fmt"
	"slices"
	"strings"
	"sync"
	"time"

	"golang.org/x/sync/errgroup"
	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/sdk/v1"
)

type projectUsage struct {
	ID                string `json:"id"`
	Name              string `json:"name"`
	Region            string `json:"region"`
	DatabaseCount     int    `json:"databaseCount"`
	SpendCapCents     int64  `json:"spendCapCents" jsonschema:"The project's own monthly spend cap in US cents; 0 means none."`
	EffectiveCapCents int64  `json:"effectiveCapCents" jsonschema:"The smallest of the organization and project caps in US cents; 0 means no limit."`
	StoredBytes       int64  `json:"storedBytes"`
	Unavailable       bool   `json:"unavailable,omitempty" jsonschema:"TerraBase did not report one of its system databases, so StoredBytes is incomplete."`
	SharePercent      int    `json:"sharePercent" jsonschema:"The project's share of the organization's stored bytes."`
	Selected          bool   `json:"selected"`
}

type databaseUsage struct {
	ID                string `json:"id"`
	Name              string `json:"name"`
	Region            string `json:"region"`
	Status            string `json:"status"`
	Documents         int    `json:"documents"`
	StoredBytes       int64  `json:"storedBytes"`
	Unavailable       bool   `json:"unavailable,omitempty" jsonschema:"TerraBase did not report this system database, so its counts are unknown."`
	SharePercent      int    `json:"sharePercent" jsonschema:"The database's share of the listed databases' stored bytes."`
	SpendCapCents     int64  `json:"spendCapCents" jsonschema:"The database's own monthly spend cap in US cents; 0 means none."`
	EffectiveCapCents int64  `json:"effectiveCapCents" jsonschema:"The smallest of the organization, project, and database caps in US cents; 0 means no limit."`
}

type usageTotals struct {
	Available bool   `json:"available" jsonschema:"Whether usage totals could be loaded."`
	Source    string `json:"source"`
	Error     string `json:"error,omitempty"`
	Requests  int64  `json:"requests"`
	Reads     int64  `json:"reads"`
	Writes    int64  `json:"writes"`
}

// usageReport is an organization's plan allowances, spending controls, and
// the selected project's database usage. The dashboard does not meter usage;
// totals come from the provider, or from sample data in demo mode.
type usageReport struct {
	OrganizationID           string          `json:"organizationId"`
	ProjectID                string          `json:"projectId"`
	DatabaseID               string          `json:"databaseId,omitempty"`
	PlanName                 string          `json:"planName"`
	PlanStorageAllowance     string          `json:"planStorageAllowance"`
	PlanRequestAllowance     string          `json:"planRequestAllowance"`
	PlanUsageNote            string          `json:"planUsageNote"`
	SpendCapCents            int64           `json:"spendCapCents" jsonschema:"The organization's monthly spend cap in US cents; 0 means none."`
	PaidExcessEnabled        bool            `json:"paidExcessEnabled" jsonschema:"Whether usage above the plan allowance is billed instead of blocked."`
	ProjectEffectiveCapCents int64           `json:"projectEffectiveCapCents" jsonschema:"The smallest of the organization and selected project caps in US cents; 0 means no limit."`
	Projects                 []projectUsage  `json:"projects"`
	Databases                []databaseUsage `json:"databases"`
	Totals                   usageTotals     `json:"totals"`
	ActiveDatabases          int             `json:"activeDatabases"`
	RegionCount              int             `json:"regionCount"`
	CanManage                bool            `json:"canManage" jsonschema:"Whether the caller may change spending controls."`
}

// spendingControls changes spend caps in US cents; 0 removes a cap. Nil
// organization fields and absent IDs leave those caps unchanged.
type spendingControls struct {
	OrganizationCapCents *int64
	PaidExcess           *bool
	ProjectCaps          map[string]int64
	DatabaseCaps         map[string]int64
}

// Service owns usage reporting and spending controls for a principal's
// selected organization. HTTP handlers and MCP tools both call it.
type Service struct {
	deps core.Deps
}

// Usage reports the selected project's usage, optionally for one database.
func (s Service) Usage(ctx context.Context, p core.Principal, databaseID string) (usageReport, error) {
	account, err := s.deps.Store.SnapshotAs(ctx, p)
	if err != nil {
		return usageReport{}, err
	}
	org, err := account.OrganizationFor(p, p.OrganizationID)
	if err != nil {
		return usageReport{}, err
	}
	project := core.Project{}
	projectID := p.ProjectID
	if projectID == "" {
		projectID = org.DefaultProjectID()
	}
	if projectID != "" {
		project, err = account.ProjectFor(p, org.ID, projectID)
		if err != nil {
			return usageReport{}, err
		}
	}
	if _, exists := project.Databases[databaseID]; databaseID != "" && !exists {
		return usageReport{}, fmt.Errorf("%w: database %q", core.ErrNotFound, databaseID)
	}
	return s.report(ctx, p, org, project, databaseID)
}

// report describes the usage of org and project. A plan the store does not
// know reads as no billing plan; any other plan lookup failure is returned.
func (s Service) report(ctx context.Context, p core.Principal, org core.Organization, project core.Project, databaseFilter string) (usageReport, error) {
	report := usageReport{
		OrganizationID: org.ID, ProjectID: project.ID, DatabaseID: databaseFilter,
		PlanName: "No billing plan", PlanStorageAllowance: "Not configured", PlanRequestAllowance: "Not configured",
		PlanUsageNote: "Provider totals have no billing-period attribution, so allowance utilization is unavailable.",
		SpendCapCents: org.Billing.SpendCapCents, PaidExcessEnabled: org.Billing.PaidExcessEnabled,
		ProjectEffectiveCapCents: effectiveCapCents(org.Billing.SpendCapCents, project.SpendCapCents),
		Projects:                 make([]projectUsage, 0, len(org.Projects)), Databases: make([]databaseUsage, 0, len(project.Databases)),
		ActiveDatabases: len(project.Databases), CanManage: org.CanManage(p.UserID),
	}
	plan, err := s.deps.Store.Plan(ctx, org.PlanID)
	switch {
	case err == nil:
		report.PlanName, report.PlanStorageAllowance, report.PlanRequestAllowance = plan.Name, plan.StorageAllowance(), plan.OperationsAllowance()
	case !errors.Is(err, core.ErrNotFound):
		return usageReport{}, err
	}
	live, failed := s.systemUsage(ctx, p, org)
	documents := func(database core.Database) int {
		if core.SystemDatabase(database) {
			return int(live[database.ID].Documents)
		}
		return documentCount(database)
	}
	stored := func(database core.Database) int64 {
		if core.SystemDatabase(database) {
			return live[database.ID].StorageBytes
		}
		return storedDocumentBytes(database)
	}
	var projectStoredBytes, databaseStoredBytes int64
	for _, item := range org.Projects {
		row := projectUsage{
			ID: item.ID, Name: item.Name, Region: item.Region, DatabaseCount: len(item.Databases), SpendCapCents: max(item.SpendCapCents, 0),
			EffectiveCapCents: effectiveCapCents(org.Billing.SpendCapCents, item.SpendCapCents), Selected: item.ID == project.ID,
		}
		if strings.TrimSpace(row.Region) == "" {
			row.Region = "Not configured"
		}
		for _, database := range item.Databases {
			row.StoredBytes += stored(database)
			if failed[database.ID] != nil {
				row.Unavailable = true
			}
		}
		projectStoredBytes += row.StoredBytes
		report.Projects = append(report.Projects, row)
	}
	regions := make(map[string]struct{})
	for _, database := range project.Databases {
		if database.Region != "" {
			regions[database.Region] = struct{}{}
		}
		if databaseFilter != "" && database.ID != databaseFilter {
			continue
		}
		row := databaseUsage{
			ID: database.ID, Name: database.Name, Region: database.Region, Status: database.Status,
			Documents: documents(database), StoredBytes: stored(database), Unavailable: failed[database.ID] != nil, SpendCapCents: max(database.SpendCapCents, 0),
			EffectiveCapCents: effectiveCapCents(org.Billing.SpendCapCents, project.SpendCapCents, database.SpendCapCents),
		}
		databaseStoredBytes += row.StoredBytes
		report.Databases = append(report.Databases, row)
	}
	report.RegionCount = len(regions)
	// A share of a total with an unknown part would be wrong: leave every
	// share at 0 once one part is unavailable.
	if !slices.ContainsFunc(report.Projects, func(row projectUsage) bool { return row.Unavailable }) {
		for i := range report.Projects {
			report.Projects[i].SharePercent = sharePercent(report.Projects[i].StoredBytes, projectStoredBytes)
		}
	}
	if !slices.ContainsFunc(report.Databases, func(row databaseUsage) bool { return row.Unavailable }) {
		for i := range report.Databases {
			report.Databases[i].SharePercent = sharePercent(report.Databases[i].StoredBytes, databaseStoredBytes)
		}
	}
	slices.SortFunc(report.Projects, func(a, b projectUsage) int { return strings.Compare(a.Name, b.Name) })
	slices.SortFunc(report.Databases, func(a, b databaseUsage) int { return strings.Compare(a.Name, b.Name) })
	totals(project, databaseFilter, live, failed, &report)
	return report, nil
}

// systemUsageBudget bounds every TerraBase lookup behind one usage report,
// so a slow TerraBase marks databases unavailable instead of holding it.
var systemUsageBudget = 5 * time.Second

// systemUsage reads the usage of the organization's system databases from
// TerraBase, keyed by database ID, a few at a time under one deadline. Each
// database is read on its own, so one failure marks only that database
// unavailable.
func (s Service) systemUsage(ctx context.Context, p core.Principal, org core.Organization) (live map[string]v1.DatabaseUsage, failed map[string]error) {
	live, failed = make(map[string]v1.DatabaseUsage), make(map[string]error)
	var system []core.Database
	for _, project := range org.Projects {
		for _, database := range project.Databases {
			if core.SystemDatabase(database) {
				system = append(system, database)
			}
		}
	}
	if len(system) == 0 {
		return live, failed
	}
	management, err := s.deps.System(ctx, p)
	if err != nil {
		for _, database := range system {
			failed[database.ID] = err
		}
		return live, failed
	}
	ctx, cancel := context.WithTimeout(ctx, systemUsageBudget)
	defer cancel()
	var mu sync.Mutex
	var group errgroup.Group
	group.SetLimit(4)
	for _, database := range system {
		group.Go(func() error {
			usage, err := management.DatabaseUsage(ctx, org.ID, database.ProjectID, database.ID)
			mu.Lock()
			defer mu.Unlock()
			if err != nil {
				failed[database.ID] = core.ProviderError{Action: "system database usage", Err: err}
			} else {
				live[database.ID] = *usage
			}
			return nil
		})
	}
	_ = group.Wait()
	return live, failed
}

// totals fills the usage totals for the project, or its one database. With a
// system database in scope they are TerraBase's metered reads and writes of
// the system databases only; otherwise sample data from the dashboard's own
// records.
func totals(project core.Project, databaseFilter string, live map[string]v1.DatabaseUsage, failed map[string]error, report *usageReport) {
	totals := &report.Totals
	var system, local bool
	var sample int64
	for _, database := range project.Databases {
		if databaseFilter != "" && database.ID != databaseFilter {
			continue
		}
		if !core.SystemDatabase(database) {
			local = true
			sample += int64(documentCount(database))
			continue
		}
		system = true
		if err := failed[database.ID]; err != nil && totals.Error == "" {
			totals.Error = "System database usage could not be loaded. " + core.SystemUnavailable(err)
		}
		totals.Reads += live[database.ID].Reads
		totals.Writes += live[database.ID].Writes
	}
	if !system {
		totals.Source = "Sample dashboard data; these values are not provider-metered usage."
		report.PlanUsageNote = "Dashboard-managed databases have no measured usage feed, so allowance utilization is unavailable."
		totals.Writes = sample
		totals.Available = true
		return
	}
	totals.Source = "Live usage of the system databases from TerraBase."
	if local {
		totals.Source += " Databases the dashboard manages in this project are not metered and are left out."
	}
	report.PlanUsageNote = "System databases have no billing plan, so allowance utilization is unavailable."
	if totals.Error != "" {
		totals.Reads, totals.Writes = 0, 0
		return
	}
	totals.Requests = totals.Reads + totals.Writes
	totals.Available = true
}

// SetSpendingControls updates the organization's, its projects', and its
// databases' spend caps in one change.
func (s Service) SetSpendingControls(ctx context.Context, p core.Principal, controls spendingControls) error {
	for _, cap := range controls.capValues() {
		if cap < 0 {
			return core.Invalid("spend caps must be zero or positive US cents")
		}
	}
	return s.deps.Store.UpdateAs(ctx, p, func(current *core.Account) error {
		org, err := current.OrganizationFor(p, p.OrganizationID)
		if err != nil {
			return err
		}
		if !org.CanManage(p.UserID) {
			return core.ErrForbidden
		}
		if controls.OrganizationCapCents != nil {
			org.Billing.SpendCapCents = *controls.OrganizationCapCents
		}
		if controls.PaidExcess != nil {
			org.Billing.PaidExcessEnabled = *controls.PaidExcess
		}
		for projectID, cap := range controls.ProjectCaps {
			project, exists := org.Projects[projectID]
			if !exists {
				return fmt.Errorf("%w: project %q", core.ErrNotFound, projectID)
			}
			project.SpendCapCents = cap
			org.Projects[projectID] = project
		}
		for databaseID, cap := range controls.DatabaseCaps {
			found := false
			for projectID, project := range org.Projects {
				database, exists := project.Databases[databaseID]
				if !exists {
					continue
				}
				database.SpendCapCents = cap
				project.Databases[databaseID] = database
				org.Projects[projectID] = project
				found = true
				break
			}
			if !found {
				return fmt.Errorf("%w: database %q", core.ErrNotFound, databaseID)
			}
		}
		current.Organizations[org.ID] = org
		if controls.OrganizationCapCents != nil || controls.PaidExcess != nil || len(controls.ProjectCaps) > 0 || len(controls.DatabaseCaps) > 0 {
			current.RecordAudit(p, "usage.spending_controls_updated", org.Name, "usage control", time.Now().UTC())
		}
		return nil
	})
}

func (c spendingControls) capValues() []int64 {
	var caps []int64
	if c.OrganizationCapCents != nil {
		caps = append(caps, *c.OrganizationCapCents)
	}
	for _, cap := range c.ProjectCaps {
		caps = append(caps, cap)
	}
	for _, cap := range c.DatabaseCaps {
		caps = append(caps, cap)
	}
	return caps
}

func documentCount(database core.Database) int {
	count := 0
	for _, collection := range database.Documents {
		count += len(collection)
	}
	return count
}

func storedDocumentBytes(database core.Database) int64 {
	var total int64
	for _, collection := range database.Documents {
		for _, document := range collection {
			total += int64(len(document))
		}
	}
	return total
}

func sharePercent(part, total int64) int {
	if part <= 0 || total <= 0 {
		return 0
	}
	return max(1, int(float64(part)/float64(total)*100+0.5))
}

// effectiveCapCents returns the smallest positive cap, or 0 for no limit.
func effectiveCapCents(caps ...int64) int64 {
	var smallest int64
	for _, cap := range caps {
		if cap > 0 && (smallest == 0 || cap < smallest) {
			smallest = cap
		}
	}
	return smallest
}
