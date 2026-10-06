package usage

import (
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
	err                                                  error
	scopes                                               []core.UsageScope
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
		return core.Usage{Period: core.UsagePeriod{Reads: m.monthReads, Writes: m.monthWrites}}, nil
	}
	database := core.Usage{DatabaseID: m.databaseID, LogicalBytes: m.logicalBytes, Documents: 2, Period: core.UsagePeriod{Reads: m.reads, Writes: m.writes}}
	return core.Usage{
		Period: database.Period, LogicalBytes: m.logicalBytes, Items: []core.Usage{database},
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
	if report.Totals.Available || report.Allowances[0].Measured || !strings.Contains(report.Totals.Source, "doesn't store this organization") {
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
