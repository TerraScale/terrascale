package usage

import (
	"cmp"
	"context"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/core/memstore"
)

// fakeMeter answers monthly project usage and the selected scope's range.
type fakeMeter struct {
	reads, writes, monthReads, monthWrites, logicalBytes int64
	databaseID                                           string
	// organizationBytes and projectBytes are TerraBase's own sums, which
	// may count resources the dashboard doesn't list.
	organizationBytes, projectBytes int64
	// err fails every call; orgErr and scopeErr fail monthly or selected-range
	// calls, preserving the single-project fixtures' separate answers.
	err, orgErr, scopeErr error
	scopes                []core.UsageScope
	monthlyUsage          map[string]core.Usage
	monthlyErrors         map[string]error
}

func (m *fakeMeter) Usage(_ context.Context, token string, scope core.UsageScope) (core.Usage, error) {
	if token != "user-token" {
		return core.Usage{}, core.ErrUnauthorized
	}
	m.scopes = append(m.scopes, scope)
	if m.err != nil {
		return core.Usage{}, m.err
	}
	if scope.Granularity == "day" && scope.From.Day() == 1 {
		if m.monthlyUsage != nil {
			return m.monthlyUsage[scope.ProjectID], m.monthlyErrors[scope.ProjectID]
		}
		return core.Usage{LogicalBytes: m.organizationBytes, Period: core.UsagePeriod{Reads: m.monthReads, Writes: m.monthWrites}}, m.orgErr
	}
	if m.scopeErr != nil {
		return core.Usage{}, m.scopeErr
	}
	database := core.Usage{DatabaseID: m.databaseID, LogicalBytes: m.logicalBytes, Documents: 2, Period: core.UsagePeriod{Reads: m.reads, Writes: m.writes}}
	return core.Usage{
		Period: database.Period, LogicalBytes: cmp.Or(m.projectBytes, m.logicalBytes), Items: []core.Usage{database},
		Series: []core.UsagePoint{{Start: scope.From, Reads: m.reads - 1, Writes: m.writes}, {Start: scope.From.Add(time.Hour), Reads: 1}},
	}, nil
}

// meterFixture is a Free organization owner with one database whose reads
// and writes the fake meter reports.
func meterFixture(t *testing.T, meter *fakeMeter) (Service, core.Principal, *http.ServeMux) {
	t.Helper()
	store := memstore.New()
	account, err := store.CreateAccount(t.Context(), core.User{Name: "Owner", Email: "meter@example.test", PasswordHash: "unused"})
	if err != nil {
		t.Fatal(err)
	}
	principal := account.Principal()
	meter.databaseID = "db_metered"
	if err := store.UpdateAs(t.Context(), principal, func(a *core.Account) error {
		org := a.Organizations[principal.OrganizationID]
		project := org.Projects[principal.ProjectID]
		project.Databases = map[string]core.Database{meter.databaseID: {ID: meter.databaseID, ProjectID: project.ID, Name: "test", Region: "GRU", Status: "ready"}}
		org.Projects[project.ID] = project
		a.Organizations[org.ID] = org
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	deps := core.Deps{Store: store, Sessions: reviewSessions{principal: principal}, Render: viewRenderer{}, Tools: core.NewTools(store), Usage: meter}
	mux := http.NewServeMux()
	Mount(mux, deps, nil)
	// The app adds the session's ts-auth token to every request's context.
	withToken := http.NewServeMux()
	withToken.Handle("/", http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		mux.ServeHTTP(w, r.WithContext(core.WithUserToken(r.Context(), func() (string, bool) { return "user-token", true })))
	}))
	return Service{deps: deps}, principal, withToken
}

func TestUsageShowsMeteredReadsAndWritesSeparately(t *testing.T) {
	meter := &fakeMeter{reads: 7, writes: 3, monthReads: 25_000, monthWrites: 10, logicalBytes: 2048}
	service, principal, mux := meterFixture(t, meter)

	report, err := service.Usage(t.Context(), principal, "user-token", "", "24h")
	if err != nil {
		t.Fatal(err)
	}
	if !report.Totals.Available || report.Totals.Reads != 7 || report.Totals.Writes != 3 || len(report.Totals.Series) != 2 {
		t.Fatalf("totals = %+v, want 7 reads and 3 writes over 2 buckets", report.Totals)
	}
	if row := report.Databases[0]; !row.Metered || row.Reads != 7 || row.Writes != 3 || row.LogicalBytes != 2048 || row.Documents != 2 {
		t.Fatalf("database row = %+v", row)
	}
	// Free includes 100K reads and 100K writes a month (DefaultPlans).
	if reads, writes := report.Allowances[0], report.Allowances[1]; reads.Used != 25_000 || reads.Limit != 100_000 || reads.Percent != 25 ||
		writes.Used != 10 || writes.Percent != 0 || !writes.Measured {
		t.Fatalf("allowances = %+v", report.Allowances)
	}
	// TerraBase knows the project under the IDs derived from the dashboard's.
	org, project := meter.scopes[0], meter.scopes[1]
	terraBaseOrg, terraBaseProject := core.Project{ID: principal.ProjectID}.TerraBaseIDs(principal.OrganizationID)
	if org.OrganizationID != terraBaseOrg || org.ProjectID != terraBaseProject || org.DatabaseID != "" || org.Granularity != "day" || org.From.Day() != 1 ||
		project.ProjectID != terraBaseProject || project.Granularity != "hour" || time.Since(project.From) > 24*time.Hour {
		t.Fatalf("scopes = %+v", meter.scopes)
	}

	response := httptest.NewRecorder()
	mux.ServeHTTP(response, httptest.NewRequest(http.MethodGet, "/usage?range=24h", nil))
	body := response.Body.String()
	if response.Code != http.StatusOK {
		t.Fatalf("GET /usage = %d: %s", response.Code, body)
	}
	for _, want := range []string{"Reads this month", "25,000", "100K reads/month", "Writes this month", "Reads and writes, Last 24 hours", "Metered by TerraBase."} {
		if !strings.Contains(body, want) {
			t.Errorf("usage page lacks %q", want)
		}
	}
	for _, gone := range []string{"Read and write operations", "Write units", "Sample dashboard data", "not metered logical storage"} {
		if strings.Contains(body, gone) {
			t.Errorf("usage page still shows %q", gone)
		}
	}
}

func TestUsageExplainsUnmeteredScopes(t *testing.T) {
	meter := &fakeMeter{err: core.ErrNotMetered}
	service, principal, mux := meterFixture(t, meter)
	report, err := service.Usage(t.Context(), principal, "user-token", "", "")
	if err != nil {
		t.Fatal(err)
	}
	if report.Totals.Available || report.Totals.StorageMeasured || report.Allowances[0].Measured || report.Allowances[2].Measured ||
		!strings.Contains(report.Totals.Source, "doesn't report this project") || len(meter.scopes) != 2 {
		t.Fatalf("report = %+v", report)
	}
	if report, _ := service.Usage(t.Context(), principal, "", "", ""); report.Totals.Available || len(meter.scopes) != 2 {
		t.Fatalf("usage without a token = %+v after %d meter calls", report.Totals, len(meter.scopes))
	}
	response := httptest.NewRecorder()
	mux.ServeHTTP(response, httptest.NewRequest(http.MethodGet, "/usage", nil))
	if body := response.Body.String(); !strings.Contains(body, "Reads and writes are not metered here.") || !strings.Contains(body, "Not metered") {
		t.Fatalf("usage page does not explain the missing meter: %s", body)
	}
}

// Monthly allowances and the selected range apply independently, even when
// one answer fails and the other succeeds.
func TestUsageAppliesEachTerraBaseAnswerOnItsOwn(t *testing.T) {
	meter := &fakeMeter{monthReads: 50_000, monthWrites: 7, scopeErr: core.ErrNotMetered}
	service, principal, _ := meterFixture(t, meter)
	report, err := service.Usage(t.Context(), principal, "user-token", "", "")
	if err != nil {
		t.Fatal(err)
	}
	if reads := report.Allowances[0]; !reads.Measured || reads.Used != 50_000 || reads.Percent != 50 {
		t.Fatalf("reads allowance = %+v, want the organization's month", reads)
	}
	// A 404 may mean the user isn't a member there, so the dashboard's own
	// documents are not taken as the project's storage.
	if report.Totals.Available || report.Totals.StorageMeasured || !strings.Contains(report.Totals.Source, "doesn't report this project") {
		t.Fatalf("totals = %+v, want an unmetered project with unknown storage", report.Totals)
	}

	meter = &fakeMeter{reads: 4, writes: 2, orgErr: core.ErrUnavailable}
	service, principal, mux := meterFixture(t, meter)
	if report, err = service.Usage(t.Context(), principal, "user-token", "", ""); err != nil {
		t.Fatal(err)
	}
	if !report.Totals.Available || report.Totals.Reads != 4 || report.Totals.Error != "" || report.Allowances[0].Measured || report.Allowances[2].Measured ||
		!strings.Contains(report.AllowanceError, "could not be loaded") {
		t.Fatalf("report = %+v, want the project's range and this month's allowances reported as failed", report)
	}
	response := httptest.NewRecorder()
	mux.ServeHTTP(response, httptest.NewRequest(http.MethodGet, "/usage", nil))
	if body := response.Body.String(); !strings.Contains(body, "could not be loaded from TerraBase") || !strings.Contains(body, "Reads and writes, Last 7 days") {
		t.Fatal("usage page hides the failed monthly usage or the loaded chart")
	}
}

// TestUsageClaimsStorageOnlyWhenItIsKnown checks that without a token a
// deployment with TerraBase reports no storage figure rather than the
// documents the dashboard happens to hold.
func TestUsageClaimsStorageOnlyWhenItIsKnown(t *testing.T) {
	service, principal, _ := meterFixture(t, &fakeMeter{})
	report, err := service.Usage(t.Context(), principal, "", "", "")
	if err != nil {
		t.Fatal(err)
	}
	if report.Totals.StorageMeasured || report.Allowances[2].Measured {
		t.Fatalf("storage without a token = %+v, %+v; want it unmeasured", report.Totals, report.Allowances[2])
	}
	service.deps.Usage = nil
	if report, err = service.Usage(t.Context(), principal, "", "", ""); err != nil {
		t.Fatal(err)
	}
	if !report.Totals.StorageMeasured || !report.Allowances[2].Measured {
		t.Fatalf("storage without TerraBase = %+v; the dashboard holds every document", report.Totals)
	}
}

// TestUsageTakesStorageTotalsFromTerraBase covers resources TerraBase stores
// but the dashboard doesn't list, such as a database created after import:
// the totals use TerraBase's sums, the share bars the listed rows.
func TestUsageTakesStorageTotalsFromTerraBase(t *testing.T) {
	meter := &fakeMeter{logicalBytes: 2048, projectBytes: 5000, organizationBytes: 9000}
	service, principal, _ := meterFixture(t, meter)
	report, err := service.Usage(t.Context(), principal, "user-token", "", "")
	if err != nil {
		t.Fatal(err)
	}
	if report.Totals.LogicalBytes != 5000 || report.Allowances[2].Used != 9000 || report.Databases[0].LogicalBytes != 2048 || report.Databases[0].SharePercent != 100 {
		t.Fatalf("storage = totals %d, allowance %d, row %+v; want TerraBase's sums with rows for shares",
			report.Totals.LogicalBytes, report.Allowances[2].Used, report.Databases[0])
	}
}

// Include both a dashboard project with derived IDs and an imported project
// whose TerraBase IDs differ, so each monthly call must use its own mapping.
func twoProjectMeterFixture(t *testing.T, meter *fakeMeter) (Service, core.Principal, map[string]string) {
	t.Helper()
	service, principal, _ := meterFixture(t, meter)
	imported := core.Project{ID: "9135698a-eaf6-4000-8000-000000000001", OrganizationID: principal.OrganizationID, Name: "Imported"}
	if err := service.deps.Store.UpdateAs(t.Context(), principal, func(a *core.Account) error {
		org := a.Organizations[principal.OrganizationID]
		org.Projects[imported.ID] = imported
		a.Organizations[org.ID] = org
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	firstOrg, firstProject := (core.Project{ID: principal.ProjectID}).TerraBaseIDs(principal.OrganizationID)
	secondOrg, secondProject := imported.TerraBaseIDs(principal.OrganizationID)
	meter.monthlyUsage = map[string]core.Usage{
		firstProject:  {LogicalBytes: 3000, Reads: 900_000, Writes: 400_000, Period: core.UsagePeriod{Reads: 25_000, Writes: 10}},
		secondProject: {LogicalBytes: 6000, Reads: 800_000, Writes: 300_000, Period: core.UsagePeriod{Reads: 35_000, Writes: 20}},
	}
	return service, principal, map[string]string{firstProject: firstOrg, secondProject: secondOrg}
}

func TestUsageSumsEveryOrganizationsProject(t *testing.T) {
	meter := &fakeMeter{reads: 7, writes: 3, logicalBytes: 2048}
	service, principal, projects := twoProjectMeterFixture(t, meter)
	report, err := service.Usage(t.Context(), principal, "user-token", meter.databaseID, "24h")
	if err != nil {
		t.Fatal(err)
	}
	for i, want := range []int64{60_000, 30, 9000} {
		if row := report.Allowances[i]; !row.Measured || row.Used != want {
			t.Errorf("allowance %d = %+v, want measured usage %d", i, row, want)
		}
	}
	if !report.Totals.Available || report.Totals.Reads != 7 || report.Totals.Writes != 3 || len(report.Totals.Series) != 2 || report.AllowanceError != "" {
		t.Fatalf("selected database report = %+v", report)
	}
	for _, row := range report.Projects {
		_, projectID := (core.Project{ID: row.ID}).TerraBaseIDs(principal.OrganizationID)
		if row.LogicalBytes != meter.monthlyUsage[projectID].LogicalBytes {
			t.Errorf("project row = %+v, want its monthly storage total", row)
		}
	}
	monthly := make(map[string]int)
	var selected int
	for _, scope := range meter.scopes {
		if scope.ProjectID == "" || scope.OrganizationID != projects[scope.ProjectID] {
			t.Errorf("scope = %+v, want that project's own TerraBase IDs", scope)
		}
		if scope.Granularity == "day" && scope.From.Day() == 1 {
			monthly[scope.ProjectID]++
			if scope.DatabaseID != "" || scope.From.Hour() != 0 || scope.From.Location() != time.UTC {
				t.Errorf("monthly scope = %+v, want the entire project from UTC month start", scope)
			}
		} else {
			selected++
			_, projectID := (core.Project{ID: principal.ProjectID}).TerraBaseIDs(principal.OrganizationID)
			if scope.ProjectID != projectID || scope.DatabaseID != meter.databaseID || scope.Granularity != "hour" {
				t.Errorf("selected scope = %+v", scope)
			}
		}
	}
	for projectID := range projects {
		if monthly[projectID] != 1 {
			t.Errorf("monthly calls for %s = %d, want 1", projectID, monthly[projectID])
		}
	}
	if selected != 1 || len(meter.scopes) != 3 {
		t.Fatalf("scopes = %+v, want both projects' months and one selected database range", meter.scopes)
	}
}

func TestUsageIncompleteOrganizationNeverReportsPartialAllowances(t *testing.T) {
	for _, failure := range []struct {
		name string
		err  error
	}{
		{"unmetered", core.ErrNotMetered},
		{"unavailable", core.ErrUnavailable},
		{"unauthorized", core.ErrUnauthorized},
	} {
		t.Run(failure.name, func(t *testing.T) {
			meter := &fakeMeter{reads: 7, writes: 3, logicalBytes: 2048}
			service, principal, _ := twoProjectMeterFixture(t, meter)
			meter.monthlyErrors = map[string]error{"9135698a-eaf6-4000-8000-000000000001": failure.err}
			report, err := service.Usage(t.Context(), principal, "user-token", "", "24h")
			if err != nil {
				t.Fatal(err)
			}
			for _, allowance := range report.Allowances {
				if allowance.Measured || allowance.Percent != 0 {
					t.Errorf("allowance = %+v, want unknown organization usage", allowance)
				}
			}
			if !report.Totals.Available || !report.Totals.StorageMeasured || report.Totals.Reads != 7 || report.Totals.Writes != 3 ||
				report.Totals.LogicalBytes != 2048 || len(report.Totals.Series) != 2 || report.Totals.Error != "" || !report.Databases[0].Metered {
				t.Fatalf("selected chart = %+v, rows = %+v; want it to survive the other project's failure", report.Totals, report.Databases)
			}
			if failure.err != core.ErrNotMetered && report.AllowanceError == "" {
				t.Error("monthly usage failure has no allowance error")
			}
		})
	}
}
