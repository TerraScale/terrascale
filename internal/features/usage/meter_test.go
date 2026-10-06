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

// fakeMeter answers like TerraBase's usage routes: the organization this
// month, and the project or database over the range.
type fakeMeter struct {
	reads, writes, monthReads, monthWrites, logicalBytes int64
	databaseID                                           string
	// organizationBytes and projectBytes are TerraBase's own sums, which
	// may count resources the dashboard doesn't list.
	organizationBytes, projectBytes int64
	// err fails every call; orgErr and scopeErr only the organization's or
	// the selected scope's.
	err, orgErr, scopeErr error
	scopes                []core.UsageScope
}

func (m *fakeMeter) Usage(_ context.Context, token string, scope core.UsageScope) (core.Usage, error) {
	if token != "user-token" {
		return core.Usage{}, core.ErrUnauthorized
	}
	m.scopes = append(m.scopes, scope)
	if m.err != nil {
		return core.Usage{}, m.err
	}
	if scope.ProjectID == "" {
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

type tokenSessions struct{ reviewSessions }

func (tokenSessions) UserToken(*http.Request) string { return "user-token" }

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
	deps := core.Deps{Store: store, Sessions: tokenSessions{reviewSessions{principal: principal}}, Render: viewRenderer{}, Tools: core.NewTools(store), Usage: meter}
	mux := http.NewServeMux()
	Mount(mux, deps, nil)
	return Service{deps: deps}, principal, mux
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
	org, project := meter.scopes[0], meter.scopes[1]
	if org.ProjectID != "" || org.Granularity != "day" || org.From.Day() != 1 ||
		project.ProjectID != principal.ProjectID || project.Granularity != "hour" || time.Since(project.From) > 24*time.Hour {
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
		!strings.Contains(report.Totals.Source, "doesn't report this organization") || len(meter.scopes) != 1 {
		t.Fatalf("report = %+v", report)
	}
	if report, _ := service.Usage(t.Context(), principal, "", "", ""); report.Totals.Available || len(meter.scopes) != 1 {
		t.Fatalf("usage without a token = %+v after %d meter calls", report.Totals, len(meter.scopes))
	}
	response := httptest.NewRecorder()
	mux.ServeHTTP(response, httptest.NewRequest(http.MethodGet, "/usage", nil))
	if body := response.Body.String(); !strings.Contains(body, "Reads and writes are not metered here.") || !strings.Contains(body, "Not metered") {
		t.Fatalf("usage page does not explain the missing meter: %s", body)
	}
}

// TestUsageAppliesEachTerraBaseAnswerOnItsOwn covers a project TerraBase
// doesn't store inside an organization it does, and an organization answer
// that fails while the project's loads.
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
