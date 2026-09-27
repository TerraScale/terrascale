package usage

import (
	"context"
	"fmt"
	"math"
	"slices"
	"strconv"
	"strings"
	"time"

	"terrascale.tech/dashboard/internal/core"
	sdk "terrascale.tech/sdk"
)

type projectUsage struct {
	ID                string `json:"id"`
	Name              string `json:"name"`
	Region            string `json:"region"`
	DatabaseCount     int    `json:"databaseCount"`
	SpendCapCents     int64  `json:"spendCapCents" jsonschema:"The project's own monthly spend cap in US cents; 0 means none."`
	EffectiveCapCents int64  `json:"effectiveCapCents" jsonschema:"The smallest of the organization and project caps in US cents; 0 means no limit."`
	StoredBytes       int64  `json:"storedBytes"`
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
	SharePercent      int    `json:"sharePercent" jsonschema:"The database's share of the listed databases' stored bytes."`
	SpendCapCents     int64  `json:"spendCapCents" jsonschema:"The database's own monthly spend cap in US cents; 0 means none."`
	EffectiveCapCents int64  `json:"effectiveCapCents" jsonschema:"The smallest of the organization, project, and database caps in US cents; 0 means no limit."`
}

type usageTotals struct {
	Available          bool   `json:"available" jsonschema:"Whether usage totals could be loaded."`
	Source             string `json:"source"`
	Error              string `json:"error,omitempty"`
	Requests           int64  `json:"requests"`
	Reads              int64  `json:"reads"`
	Writes             int64  `json:"writes"`
	StorageByteSeconds int64  `json:"storageByteSeconds"`
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
	account, err := s.deps.Store.SnapshotAs(p)
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
	return s.report(ctx, p, org, project, databaseID), nil
}

func (s Service) report(ctx context.Context, p core.Principal, org core.Organization, project core.Project, databaseFilter string) usageReport {
	report := usageReport{
		OrganizationID: org.ID, ProjectID: project.ID, DatabaseID: databaseFilter,
		PlanName: "No billing plan", PlanStorageAllowance: "Not configured", PlanRequestAllowance: "Not configured",
		PlanUsageNote: "Provider totals have no billing-period attribution, so allowance utilization is unavailable.",
		SpendCapCents: org.Billing.SpendCapCents, PaidExcessEnabled: org.Billing.PaidExcessEnabled,
		ProjectEffectiveCapCents: effectiveCapCents(org.Billing.SpendCapCents, project.SpendCapCents),
		Projects:                 make([]projectUsage, 0, len(org.Projects)), Databases: make([]databaseUsage, 0, len(project.Databases)),
		ActiveDatabases: len(project.Databases), CanManage: org.CanManage(p.UserID),
	}
	if plan, ok := s.deps.Store.Plan(org.PlanID); ok {
		report.PlanName, report.PlanStorageAllowance, report.PlanRequestAllowance = plan.Name, plan.StorageAllowance(), plan.OperationsAllowance()
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
			row.StoredBytes += storedDocumentBytes(database)
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
			Documents: documentCount(database), StoredBytes: storedDocumentBytes(database), SpendCapCents: max(database.SpendCapCents, 0),
			EffectiveCapCents: effectiveCapCents(org.Billing.SpendCapCents, project.SpendCapCents, database.SpendCapCents),
		}
		databaseStoredBytes += row.StoredBytes
		report.Databases = append(report.Databases, row)
	}
	report.RegionCount = len(regions)
	for i := range report.Projects {
		report.Projects[i].SharePercent = sharePercent(report.Projects[i].StoredBytes, projectStoredBytes)
	}
	for i := range report.Databases {
		report.Databases[i].SharePercent = sharePercent(report.Databases[i].StoredBytes, databaseStoredBytes)
	}
	slices.SortFunc(report.Projects, func(a, b projectUsage) int { return strings.Compare(a.Name, b.Name) })
	slices.SortFunc(report.Databases, func(a, b databaseUsage) int { return strings.Compare(a.Name, b.Name) })
	s.totals(ctx, p, project, databaseFilter, &report)
	return report
}

// totals fills the usage totals from sample data in demo mode or from the
// provider's database usage facts.
func (s Service) totals(ctx context.Context, p core.Principal, project core.Project, databaseFilter string, report *usageReport) {
	totals := &report.Totals
	if s.deps.Demo {
		totals.Source = "Sample dashboard data; these values are not provider-metered usage."
		report.PlanUsageNote = "Demo mode has no measured usage feed, so allowance utilization is unavailable."
		totals.Writes = int64(localDocuments(project, databaseFilter))
		totals.Available = true
		return
	}
	if project.ID == "" {
		return
	}
	if s.deps.SDK == nil {
		totals.Source = "Live usage provider is not configured."
		totals.Error = "Usage totals are unavailable until the ts-go provider is connected."
		report.PlanUsageNote = "The usage provider is not configured, so allowance utilization is unavailable."
		return
	}
	client, err := s.deps.SDK.ForScope(p.OrganizationID, project.ID)
	if err != nil {
		totals.Source = "Live usage provider could not be reached."
		totals.Error = "Usage totals are unavailable for this project."
		report.PlanUsageNote = "The usage provider could not be reached, so allowance utilization is unavailable."
		return
	}
	totals.Source = "Live usage facts from TerraScale."
	fetched := len(project.Databases) == 0
	for _, database := range project.Databases {
		if databaseFilter != "" && database.ID != databaseFilter {
			continue
		}
		usage, err := client.Usage.GetDatabaseUsage(ctx, database.Name)
		if err != nil {
			totals.Error = "Some database usage could not be loaded."
			continue
		}
		fetched = true
		for _, meter := range usage.Meters {
			value, err := strconv.ParseInt(meter.Units, 10, 64)
			if err != nil || value < 0 {
				totals.Error = "The provider returned an invalid usage meter."
				continue
			}
			switch meter.Meter {
			case sdk.MeterDocumentWriteUnits:
				if !addUsage(&totals.Writes, value) {
					totals.Error = "The provider reported more writes than this page can display."
				}
			case sdk.MeterStorageByteSeconds:
				if !addUsage(&totals.StorageByteSeconds, value) {
					totals.Error = "The provider reported more storage activity than this page can display."
				}
			}
		}
		if usage.Reads.TotalReads > uint64(math.MaxInt64-totals.Reads) {
			totals.Error = "The provider reported more reads than this page can display."
		} else {
			totals.Reads += int64(usage.Reads.TotalReads)
		}
	}
	if !addUsage(&totals.Requests, totals.Reads) || !addUsage(&totals.Requests, totals.Writes) {
		totals.Requests = math.MaxInt64
		totals.Error = "The provider reported more operations than this page can display."
	}
	totals.Available = fetched
}

// SetSpendingControls updates the organization's, its projects', and its
// databases' spend caps in one change.
func (s Service) SetSpendingControls(p core.Principal, controls spendingControls) error {
	for _, cap := range controls.capValues() {
		if cap < 0 {
			return core.Invalid("spend caps must be zero or positive US cents")
		}
	}
	return s.deps.Store.UpdateAs(p, func(current *core.Account) error {
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

func localDocuments(project core.Project, databaseFilter string) int {
	count := 0
	for _, database := range project.Databases {
		if databaseFilter != "" && database.ID != databaseFilter {
			continue
		}
		count += documentCount(database)
	}
	return count
}

func sharePercent(part, total int64) int {
	if part <= 0 || total <= 0 {
		return 0
	}
	return max(1, int(float64(part)/float64(total)*100+0.5))
}

func addUsage(total *int64, value int64) bool {
	if value < 0 || *total > math.MaxInt64-value {
		return false
	}
	*total += value
	return true
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
