package usage

import (
	"cmp"
	"context"
	"errors"
	"fmt"
	"math"
	"slices"
	"strings"
	"sync"
	"time"

	"golang.org/x/sync/errgroup"
	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/features/demovisuals"
	"terrascale.tech/sdk/v1"
)

type projectUsage struct {
	OperationLimits   core.OperationLimits `json:"operationLimits"`
	ID                string               `json:"id"`
	Name              string               `json:"name"`
	Region            string               `json:"region"`
	DatabaseCount     int                  `json:"databaseCount"`
	SpendCapCents     int64                `json:"spendCapCents" jsonschema:"The project's own monthly spend cap in US cents; 0 means none."`
	EffectiveCapCents int64                `json:"effectiveCapCents" jsonschema:"The smallest of the organization and project caps in US cents; 0 means no limit."`
	LogicalBytes      int64                `json:"logicalBytes" jsonschema:"Logical storage: the bytes of the project's live documents."`
	Unavailable       bool                 `json:"unavailable,omitempty" jsonschema:"TerraBase did not report one of its system databases, so LogicalBytes is incomplete."`
	SharePercent      int                  `json:"sharePercent" jsonschema:"The project's share of the organization's logical storage."`
	Selected          bool                 `json:"selected"`
}

type databaseUsage struct {
	OperationLimits   core.OperationLimits `json:"operationLimits"`
	ID                string               `json:"id"`
	Name              string               `json:"name"`
	Region            string               `json:"region"`
	Status            string               `json:"status"`
	Documents         int64                `json:"documents"`
	LogicalBytes      int64                `json:"logicalBytes" jsonschema:"Logical storage: the bytes of the database's live documents."`
	Unavailable       bool                 `json:"unavailable,omitempty" jsonschema:"TerraBase did not report this system database, so its counts are unknown."`
	SharePercent      int                  `json:"sharePercent" jsonschema:"The database's share of the listed databases' logical storage."`
	Metered           bool                 `json:"metered" jsonschema:"Whether TerraBase meters this database; its reads and writes are 0 otherwise."`
	Reads             int64                `json:"reads" jsonschema:"Reads in the range."`
	Writes            int64                `json:"writes" jsonschema:"Writes in the range."`
	SpendCapCents     int64                `json:"spendCapCents" jsonschema:"The database's own monthly spend cap in US cents; 0 means none."`
	EffectiveCapCents int64                `json:"effectiveCapCents" jsonschema:"The smallest of the organization, project, and database caps in US cents; 0 means no limit."`
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
	InternalBill             *internalBill    `json:"internalBill,omitempty"`
	OrganizationID           string           `json:"organizationId"`
	ProjectID                string           `json:"projectId"`
	DatabaseID               string           `json:"databaseId,omitempty"`
	PlanName                 string           `json:"planName"`
	Allowances               []allowanceUsage `json:"allowances" jsonschema:"Reads, writes, and storage against the plan's limits."`
	AllowanceError           string           `json:"allowanceError,omitempty" jsonschema:"Why this month's usage failed to load, when TerraBase answered with an error."`
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
	ProjectOperations    map[string]operationLimitChange
	DatabaseOperations   map[string]operationLimitChange
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
	// System databases (TS-611) are TerraBase's: their counts come from it.
	live, failed := s.systemUsage(ctx, p, org)
	documents := func(database core.Database) int64 {
		if core.SystemDatabase(database) {
			return live[database.ID].Documents
		}
		return int64(documentCount(database))
	}
	stored := func(database core.Database) int64 {
		if core.SystemDatabase(database) {
			return live[database.ID].StorageBytes
		}
		return storedDocumentBytes(database)
	}
	// Storage starts from the documents the dashboard holds (TS-472 phase
	// 1), the same bytes TerraBase meters as logical storage; metered
	// figures replace them below where TerraBase stores the resource.
	for _, item := range org.Projects {
		row := projectUsage{
			OperationLimits: item.OperationLimits,
			ID:              item.ID, Name: item.Name, Region: item.Region, DatabaseCount: len(item.Databases), SpendCapCents: max(item.SpendCapCents, 0),
			EffectiveCapCents: effectiveCapCents(org.Billing.SpendCapCents, item.SpendCapCents), Selected: item.ID == project.ID,
		}
		if strings.TrimSpace(row.Region) == "" {
			row.Region = "Not configured"
		}
		for _, database := range item.Databases {
			row.LogicalBytes += stored(database)
			if failed[database.ID] != nil {
				row.Unavailable = true
			}
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
			OperationLimits: database.OperationLimits,
			ID:              database.ID, Name: database.Name, Region: database.Region, Status: database.Status,
			Documents: documents(database), LogicalBytes: stored(database), Unavailable: failed[database.ID] != nil, SpendCapCents: max(database.SpendCapCents, 0),
			EffectiveCapCents: effectiveCapCents(org.Billing.SpendCapCents, project.SpendCapCents, database.SpendCapCents),
		})
	}
	report.RegionCount = len(regions)
	metered := s.meter(ctx, token, org, project, databaseFilter, &report)
	// Shares compare the listed rows; totals take TerraBase's own sums when
	// it answered, which also count resources the dashboard doesn't list.
	var organizationBytes, databaseBytes int64
	for _, row := range report.Projects {
		organizationBytes += row.LogicalBytes
	}
	for _, row := range report.Databases {
		databaseBytes += row.LogicalBytes
	}
	for i := range report.Projects {
		report.Projects[i].SharePercent = sharePercent(report.Projects[i].LogicalBytes, organizationBytes)
	}
	for i := range report.Databases {
		report.Databases[i].SharePercent = sharePercent(report.Databases[i].LogicalBytes, databaseBytes)
	}
	if !report.Totals.Available {
		report.Totals.LogicalBytes = databaseBytes
	}
	if metered.monthMeasured {
		organizationBytes = metered.organizationBytes
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
	report.AllowanceError = metered.monthError
	if org.Billing.Internal {
		report.InternalBill = computeInternalBill(plan, report, metered.month)
	}
	if !metered.monthMeasured && metered.monthError == "" {
		report.PlanUsageNote = "This month's reads and writes aren't metered here, so their utilization is unavailable."
	}
	if !report.Totals.Available {
		systemTotals(project, databaseFilter, live, failed, &report)
	}
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

// systemTotals fills totals the meter could not with TerraBase's lifetime
// reads and writes of the system databases in scope, the only databases it
// reports to a platform admin without the meter (TS-611).
func systemTotals(project core.Project, databaseFilter string, live map[string]v1.DatabaseUsage, failed map[string]error, report *usageReport) {
	totals := &report.Totals
	var system bool
	var reads, writes int64
	for _, database := range project.Databases {
		if databaseFilter != "" && database.ID != databaseFilter || !core.SystemDatabase(database) {
			continue
		}
		system = true
		if err := failed[database.ID]; err != nil && totals.Error == "" {
			totals.Error = "System database usage could not be loaded. " + core.SystemUnavailable(err)
		}
		reads += live[database.ID].Reads
		writes += live[database.ID].Writes
	}
	if !system || totals.Error != "" {
		return
	}
	totals.Available, totals.Source = true, "Lifetime usage of the system databases from TerraBase."
	totals.Reads, totals.Writes = reads, writes
}

// metered is what TerraBase answered for a report: the organization's reads
// and writes this month, and whether its projects' storage is known.
type metered struct {
	month               core.UsagePeriod
	organizationBytes   int64
	monthMeasured       bool
	organizationStorage bool
	// monthError is why the organization's answer failed, other than
	// TerraBase not reporting the organization.
	monthError string
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
	orgID, projectID := project.TerraBaseIDs(org.ID)
	terraBaseID := func(databaseID string) string {
		return cmp.Or(project.Databases[databaseID].TerraBaseID, databaseID)
	}
	now := time.Now().UTC()
	monthStart := time.Date(now.Year(), now.Month(), 1, 0, 0, 0, 0, time.UTC)
	organization, err := s.organizationUsage(ctx, token, org, monthStart)
	switch {
	case err == nil:
		result.month, result.monthMeasured, result.organizationStorage = organization.Period, true, true
		result.organizationBytes = organization.LogicalBytes
		for i, row := range report.Projects {
			if item, ok := findUsage(organization.Items, func(u core.Usage) string { return u.ProjectID }, row.ID); ok {
				report.Projects[i].LogicalBytes, report.Projects[i].Unavailable = item.LogicalBytes, false
			}
		}
	case errors.Is(err, core.ErrNotMetered):
		result.monthError = "This month's organization usage is incomplete because TerraBase doesn't report every project's monthly usage."
	case errors.Is(err, core.ErrUnauthorized):
		result.monthError = "TerraBase didn't accept this sign-in, so this month's reads and writes didn't load. Sign out and in again."
	default:
		// The selected scope's answer below still applies.
		result.monthError = "This month's reads and writes could not be loaded from TerraBase. Try again shortly."
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
		OrganizationID: orgID, ProjectID: projectID, DatabaseID: terraBaseID(databaseFilter), Granularity: granularity, From: from,
	})
	if err != nil {
		meterError(totals, err, scope)
		return result
	}
	items := scoped.Items
	if databaseFilter != "" {
		scoped.DatabaseID = terraBaseID(databaseFilter)
		items = []core.Usage{scoped}
	}
	for i, row := range report.Databases {
		if item, ok := findUsage(items, func(u core.Usage) string { return u.DatabaseID }, terraBaseID(row.ID)); ok {
			report.Databases[i].Metered, report.Databases[i].Unavailable = true, false
			report.Databases[i].Documents, report.Databases[i].LogicalBytes = item.Documents, item.LogicalBytes
			report.Databases[i].Reads, report.Databases[i].Writes = item.Period.Reads, item.Period.Writes
		}
	}
	totals.Available, totals.StorageMeasured, totals.Source = true, true, "Metered by TerraBase."
	totals.Reads, totals.Writes, totals.Series = scoped.Period.Reads, scoped.Period.Writes, scoped.Series
	totals.LogicalBytes = scoped.LogicalBytes
	return result
}

// TerraBase meters projects; ts-auth owns organizations. A failed project
// makes the organization's usage unknown, never a smaller measured total.
func (s Service) organizationUsage(ctx context.Context, token string, org core.Organization, from time.Time) (core.Usage, error) {
	ctx, cancel := context.WithTimeout(ctx, systemUsageBudget)
	defer cancel()
	total := core.Usage{OrganizationID: org.ID, Period: core.UsagePeriod{From: from, Granularity: "day"}}
	for _, project := range org.Projects {
		orgID, projectID := project.TerraBaseIDs(org.ID)
		usage, err := s.deps.Usage.Usage(ctx, token, core.UsageScope{OrganizationID: orgID, ProjectID: projectID, Granularity: "day", From: from})
		if err != nil {
			return core.Usage{}, err
		}
		if org.Billing.Internal {
			period := usage.Period
			if !period.From.Equal(from) || !period.To.After(from) || period.To.After(from.AddDate(0, 1, 0)) {
				return core.Usage{}, fmt.Errorf("%w: invalid internal billing period", core.ErrUnavailable)
			}
			if period.To.After(total.Period.To) {
				total.Period.To = period.To
			}
		}
		if usage.Period.Reads < 0 || usage.Period.Writes < 0 || usage.LogicalBytes < 0 ||
			usage.Period.Reads > math.MaxInt64-total.Period.Reads || usage.Period.Writes > math.MaxInt64-total.Period.Writes || usage.LogicalBytes > math.MaxInt64-total.LogicalBytes {
			return core.Usage{}, fmt.Errorf("%w: invalid organization usage totals", core.ErrUnavailable)
		}
		total.Period.Reads += usage.Period.Reads
		total.Period.Writes += usage.Period.Writes
		total.LogicalBytes += usage.LogicalBytes
		// Rows use the dashboard's project IDs, while TerraBase uses the
		// derived IDs in the request above.
		usage.ProjectID = project.ID
		total.Items = append(total.Items, usage)
	}
	return total, nil
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
	for _, changes := range []map[string]operationLimitChange{controls.ProjectOperations, controls.DatabaseOperations} {
		for _, change := range changes {
			if !change.valid() {
				return core.Invalid("operation limits must be zero or positive whole numbers")
			}
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
		if controls.PaidExcess != nil && !org.Billing.Internal {
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
		for projectID, change := range controls.ProjectOperations {
			project, exists := org.Projects[projectID]
			if !exists {
				return fmt.Errorf("%w: project %q", core.ErrNotFound, projectID)
			}
			change.apply(&project.OperationLimits)
			org.Projects[projectID] = project
		}
		for databaseID, change := range controls.DatabaseOperations {
			found := false
			for projectID, project := range org.Projects {
				database, exists := project.Databases[databaseID]
				if !exists {
					continue
				}
				change.apply(&database.OperationLimits)
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
		if controls.OrganizationCapCents != nil || controls.PaidExcess != nil || len(controls.ProjectCaps) > 0 || len(controls.DatabaseCaps) > 0 || len(controls.ProjectOperations) > 0 || len(controls.DatabaseOperations) > 0 {
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
