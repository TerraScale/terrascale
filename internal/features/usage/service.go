package usage

import (
	"context"
	"errors"
	"fmt"
	"math"
	"slices"
	"strings"
	"time"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/features/demovisuals"
)

type projectUsage struct {
	ID                string `json:"id"`
	Name              string `json:"name"`
	Region            string `json:"region"`
	DatabaseCount     int    `json:"databaseCount"`
	SpendCapCents     int64  `json:"spendCapCents" jsonschema:"The project's own monthly spend cap in US cents; 0 means none."`
	EffectiveCapCents int64  `json:"effectiveCapCents" jsonschema:"The smallest of the organization and project caps in US cents; 0 means no limit."`
	LogicalBytes      int64  `json:"logicalBytes" jsonschema:"Logical storage: the bytes of the project's live documents."`
	SharePercent      int    `json:"sharePercent" jsonschema:"The project's share of the organization's logical storage."`
	Selected          bool   `json:"selected"`
}

type databaseUsage struct {
	ID                string `json:"id"`
	Name              string `json:"name"`
	Region            string `json:"region"`
	Status            string `json:"status"`
	Documents         int64  `json:"documents"`
	LogicalBytes      int64  `json:"logicalBytes" jsonschema:"Logical storage: the bytes of the database's live documents."`
	SharePercent      int    `json:"sharePercent" jsonschema:"The database's share of the listed databases' logical storage."`
	Metered           bool   `json:"metered" jsonschema:"Whether TerraBase meters this database; its reads and writes are 0 otherwise."`
	Reads             int64  `json:"reads" jsonschema:"Reads in the range."`
	Writes            int64  `json:"writes" jsonschema:"Writes in the range."`
	SpendCapCents     int64  `json:"spendCapCents" jsonschema:"The database's own monthly spend cap in US cents; 0 means none."`
	EffectiveCapCents int64  `json:"effectiveCapCents" jsonschema:"The smallest of the organization, project, and database caps in US cents; 0 means no limit."`
}

// usageTotals are the selected project's, or database's, metered reads and
// writes in the range. Each document get, list page or query page is one
// read; each committed put, patch or delete is one write.
type usageTotals struct {
	Available    bool   `json:"available" jsonschema:"Whether TerraBase's metered reads and writes could be loaded."`
	Source       string `json:"source"`
	Error        string `json:"error,omitempty"`
	Range        string `json:"range" jsonschema:"24h, 7d, or 30d."`
	Reads        int64  `json:"reads"`
	Writes       int64  `json:"writes"`
	LogicalBytes int64  `json:"logicalBytes" jsonschema:"Logical storage of the selected scope."`
	// StorageMeasured is unset when TerraBase stores the scope but could not
	// be asked; LogicalBytes and document counts are then not known.
	StorageMeasured bool              `json:"storageMeasured" jsonschema:"Whether LogicalBytes and document counts are known: TerraBase measured them, or the dashboard holds the documents."`
	Series          []core.UsagePoint `json:"series" jsonschema:"Reads and writes per UTC hour (24h, 7d) or day (30d), oldest first."`
}

// allowanceUsage is one plan allowance and how much of it the organization
// used this calendar month (UTC).
type allowanceUsage struct {
	Name      string `json:"name"`
	Allowance string `json:"allowance"`
	Limit     int64  `json:"limit" jsonschema:"The plan's limit; 0 when it publishes none."`
	Used      int64  `json:"used"`
	Measured  bool   `json:"measured" jsonschema:"Whether Used was measured."`
	Percent   int    `json:"percent" jsonschema:"Used as a percentage of Limit, when both are known."`
}

// usageReport is an organization's plan allowances, spending controls, and
// the selected project's database usage. TerraBase meters reads, writes and
// storage; the dashboard only reads them.
type usageReport struct {
	OrganizationID           string           `json:"organizationId"`
	ProjectID                string           `json:"projectId"`
	DatabaseID               string           `json:"databaseId,omitempty"`
	PlanName                 string           `json:"planName"`
	Allowances               []allowanceUsage `json:"allowances" jsonschema:"Reads, writes, and storage against the plan's limits."`
	PlanUsageNote            string           `json:"planUsageNote"`
	SpendCapCents            int64            `json:"spendCapCents" jsonschema:"The organization's monthly spend cap in US cents; 0 means none."`
	PaidExcessEnabled        bool             `json:"paidExcessEnabled" jsonschema:"Whether usage above the plan allowance is billed instead of blocked."`
	ProjectEffectiveCapCents int64            `json:"projectEffectiveCapCents" jsonschema:"The smallest of the organization and selected project caps in US cents; 0 means no limit."`
	Projects                 []projectUsage   `json:"projects"`
	Databases                []databaseUsage  `json:"databases"`
	Totals                   usageTotals      `json:"totals"`
	ActiveDatabases          int              `json:"activeDatabases"`
	RegionCount              int              `json:"regionCount"`
	CanManage                bool             `json:"canManage" jsonschema:"Whether the caller may change spending controls."`
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

// Usage reports the selected project's usage over rangeKey (24h, 7d or
// 30d), optionally for one database. token is the user's ts-auth access
// token, with which TerraBase's metered usage is read; without one, only
// stored resources are reported.
func (s Service) Usage(ctx context.Context, p core.Principal, token, databaseID, rangeKey string) (usageReport, error) {
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
	return s.report(ctx, p, token, org, project, databaseID, rangeKey)
}

// report describes the usage of org and project. A plan the store does not
// know reads as no billing plan; any other plan lookup failure is returned.
func (s Service) report(ctx context.Context, p core.Principal, token string, org core.Organization, project core.Project, databaseFilter, rangeKey string) (usageReport, error) {
	report := usageReport{
		OrganizationID: org.ID, ProjectID: project.ID, DatabaseID: databaseFilter, PlanName: "No billing plan",
		SpendCapCents: org.Billing.SpendCapCents, PaidExcessEnabled: org.Billing.PaidExcessEnabled,
		ProjectEffectiveCapCents: effectiveCapCents(org.Billing.SpendCapCents, project.SpendCapCents),
		Projects:                 make([]projectUsage, 0, len(org.Projects)), Databases: make([]databaseUsage, 0, len(project.Databases)),
		ActiveDatabases: len(project.Databases), CanManage: org.CanManage(p.UserID),
		Totals: usageTotals{Range: demovisuals.NormalizeRange(rangeKey)},
	}
	plan, err := s.deps.Store.Plan(ctx, org.PlanID)
	switch {
	case err == nil:
		report.PlanName = plan.Name
	case !errors.Is(err, core.ErrNotFound):
		return usageReport{}, err
	}
	// Storage starts from the documents the dashboard holds (TS-472 phase
	// 1), the same bytes TerraBase meters as logical storage; metered
	// figures replace them below where TerraBase stores the resource.
	for _, item := range org.Projects {
		row := projectUsage{
			ID: item.ID, Name: item.Name, Region: item.Region, DatabaseCount: len(item.Databases), SpendCapCents: max(item.SpendCapCents, 0),
			EffectiveCapCents: effectiveCapCents(org.Billing.SpendCapCents, item.SpendCapCents), Selected: item.ID == project.ID,
		}
		if strings.TrimSpace(row.Region) == "" {
			row.Region = "Not configured"
		}
		for _, database := range item.Databases {
			row.LogicalBytes += storedDocumentBytes(database)
		}
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
		report.Databases = append(report.Databases, databaseUsage{
			ID: database.ID, Name: database.Name, Region: database.Region, Status: database.Status,
			Documents: int64(documentCount(database)), LogicalBytes: storedDocumentBytes(database), SpendCapCents: max(database.SpendCapCents, 0),
			EffectiveCapCents: effectiveCapCents(org.Billing.SpendCapCents, project.SpendCapCents, database.SpendCapCents),
		})
	}
	report.RegionCount = len(regions)
	metered := s.meter(ctx, token, org, project, databaseFilter, &report)
	var organizationBytes, databaseBytes int64
	for _, row := range report.Projects {
		organizationBytes += row.LogicalBytes
	}
	for _, row := range report.Databases {
		databaseBytes += row.LogicalBytes
		report.Totals.LogicalBytes += row.LogicalBytes
	}
	for i := range report.Projects {
		report.Projects[i].SharePercent = sharePercent(report.Projects[i].LogicalBytes, organizationBytes)
	}
	for i := range report.Databases {
		report.Databases[i].SharePercent = sharePercent(report.Databases[i].LogicalBytes, databaseBytes)
	}
	slices.SortFunc(report.Projects, func(a, b projectUsage) int { return strings.Compare(a.Name, b.Name) })
	slices.SortFunc(report.Databases, func(a, b databaseUsage) int { return strings.Compare(a.Name, b.Name) })
	// ponytail: storage counts each byte once; multiply by a database's
	// regions once databases span several, as GB-region allowances do.
	report.Allowances = []allowanceUsage{
		allowance("Reads this month", plan.ReadsAllowance(), plan.MonthlyOperations(), metered.month.Reads, metered.monthMeasured),
		allowance("Writes this month", plan.WritesAllowance(), plan.MonthlyOperations(), metered.month.Writes, metered.monthMeasured),
		allowance("Storage", plan.StorageAllowance(), plan.StorageBytes(), organizationBytes, metered.organizationStorage),
	}
	report.PlanUsageNote = "Reads and writes count separately: each read is one read and each write one write. The month runs on UTC."
	if !metered.monthMeasured {
		report.PlanUsageNote = "This month's reads and writes aren't metered here, so their utilization is unavailable."
	}
	return report, nil
}

// metered is what TerraBase answered for a report: the organization's reads
// and writes this month, and whether its projects' storage is known.
type metered struct {
	month               core.UsagePeriod
	monthMeasured       bool
	organizationStorage bool
}

// meter fills the report with TerraBase's metered usage. The organization's
// answer gives this month's reads and writes and its projects' storage; the
// selected project's or database's answer gives the range's totals, series
// and databases. Each applies on its own. Storage counts as known where
// TerraBase measured it, or without TerraBase, where the dashboard holds every
// document. TerraBase's 404 does not say which: the resource may not be
// stored there (TS-472 phase 1) or the user may not be a member there.
func (s Service) meter(ctx context.Context, token string, org core.Organization, project core.Project, databaseFilter string, report *usageReport) metered {
	totals := &report.Totals
	var result metered
	switch {
	case s.deps.Usage == nil:
		totals.Source = "This deployment has no TerraBase, which meters reads and writes."
		totals.StorageMeasured, result.organizationStorage = true, true
		return result
	case token == "":
		totals.Source = "Metered reads and writes load in a browser session signed in through TerraScale."
		return result
	}
	now := time.Now().UTC()
	monthStart := time.Date(now.Year(), now.Month(), 1, 0, 0, 0, 0, time.UTC)
	organization, err := s.deps.Usage.Usage(ctx, token, core.UsageScope{OrganizationID: org.ID, Granularity: "day", From: monthStart})
	switch {
	case err == nil:
		result.month, result.monthMeasured, result.organizationStorage = organization.Period, true, true
		for i, row := range report.Projects {
			if item, ok := findUsage(organization.Items, func(u core.Usage) string { return u.ProjectID }, row.ID); ok {
				report.Projects[i].LogicalBytes = item.LogicalBytes
			}
		}
	case errors.Is(err, core.ErrNotMetered):
		// TerraBase reports none of the organization's projects either.
		meterError(totals, err, "organization")
		return result
	}
	if project.ID == "" {
		totals.Source = "Create a project to meter its reads and writes."
		return result
	}
	scope := "project"
	if databaseFilter != "" {
		scope = "database"
	}
	granularity, from := core.UsageBuckets(totals.Range, now)
	scoped, err := s.deps.Usage.Usage(ctx, token, core.UsageScope{
		OrganizationID: org.ID, ProjectID: project.ID, DatabaseID: databaseFilter, Granularity: granularity, From: from,
	})
	if err != nil {
		meterError(totals, err, scope)
		return result
	}
	items := scoped.Items
	if databaseFilter != "" {
		scoped.DatabaseID = databaseFilter
		items = []core.Usage{scoped}
	}
	for i, row := range report.Databases {
		if item, ok := findUsage(items, func(u core.Usage) string { return u.DatabaseID }, row.ID); ok {
			report.Databases[i].Metered = true
			report.Databases[i].Documents, report.Databases[i].LogicalBytes = item.Documents, item.LogicalBytes
			report.Databases[i].Reads, report.Databases[i].Writes = item.Period.Reads, item.Period.Writes
		}
	}
	totals.Available, totals.StorageMeasured, totals.Source = true, true, "Metered by TerraBase."
	totals.Reads, totals.Writes, totals.Series = scoped.Period.Reads, scoped.Period.Writes, scoped.Series
	return result
}

// meterError explains why TerraBase's usage of the scope ("organization",
// "project" or "database") could not be read.
func meterError(totals *usageTotals, err error, scope string) {
	switch {
	case errors.Is(err, core.ErrNotMetered):
		totals.Source = "TerraBase doesn't report this " + scope + ": it isn't stored there yet, or this account isn't a member of it there."
	case errors.Is(err, core.ErrUnauthorized):
		totals.Source = "TerraBase didn't accept this sign-in. Sign out and in again to load metered reads and writes."
	default:
		totals.Source = "TerraBase metering is unavailable."
		totals.Error = "Metered reads and writes could not be loaded. Try again shortly."
	}
}

func findUsage(items []core.Usage, id func(core.Usage) string, want string) (core.Usage, bool) {
	index := slices.IndexFunc(items, func(item core.Usage) bool { return id(item) == want })
	if index < 0 {
		return core.Usage{}, false
	}
	return items[index], true
}

func allowance(name, label string, limit, used int64, measured bool) allowanceUsage {
	row := allowanceUsage{Name: name, Allowance: label, Limit: limit, Used: used, Measured: measured}
	if measured && limit > 0 {
		row.Percent = int(min(float64(used)/float64(limit)*100+0.5, math.MaxInt32))
	}
	return row
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
