package e2e

import (
	"net/http"
	"net/http/httptest"
	"net/url"
	"slices"
	"strings"
	"sync/atomic"
	"testing"

	"github.com/mxschmitt/playwright-go"
	"terrascale.tech/dashboard/internal/adapters/terrabase"
	"terrascale.tech/dashboard/internal/app"
	"terrascale.tech/dashboard/internal/platform/config"
	"terrascale.tech/dashboard/internal/platform/terrabasetest"
	"terrascale.tech/sdk/v1"
)

// Create through the browser, read through ts-go, then discard every
// process-local store by restarting the dashboard on the same TerraBase.
func TestAccountAndSessionSurviveDashboardRestart(t *testing.T) {
	if testing.Short() {
		t.Skip("starts pinned TerraBase and Chromium")
	}
	tb := terrabasetest.Start(t)
	db, key := terrabasetest.Database(t, tb, "dashboard")
	must(t, terrabase.EnsureCollections(t.Context(), db))
	must(t, terrabase.New(db).SeedPlans(t.Context()))
	cfg := config.Config{
		TerraBaseURL: tb.URL, TerraBaseAPIKey: key, TerraBaseProjectID: db.Project(), TerraBaseDatabase: db.Name(),
		TerraBaseHTTPClient: tb.Client, CSRFSecret: []byte(strings.Repeat("r", 32)),
	}
	application, err := app.New(cfg)
	must(t, err)
	var active atomic.Pointer[app.Application]
	active.Store(application)
	dashboard := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		active.Load().Handler.ServeHTTP(w, r)
	}))
	t.Cleanup(dashboard.Close)
	page := watchedPage(t, newContext(t, dashboard.URL))
	account := newAccount("terrabase-restart")
	registerAndOnboard(t, page, account)
	stored, err := terrabase.New(db).FindByEmail(t.Context(), account.Email)
	must(t, err)
	root, err := db.Collection("users").Get(t.Context(), v1.DocumentIDFor("users", stored.ID))
	must(t, err)
	if !strings.Contains(string(root.Document), account.Email) {
		t.Fatal("account missing from real document")
	}
	before := sessionToken(t, page.Context())
	application, err = app.New(cfg)
	must(t, err)
	active.Store(application)
	goToOK(t, page, "/")
	visible(t, pageByRole(page, *playwright.AriaRoleHeading, "Overview").First())
	if got := sessionToken(t, page.Context()); got != before {
		t.Fatal("session changed across restart")
	}
}

// A database's regions persist in the TerraBase-backed store: create one
// replicated across GRU and SFO, move it to GRU only, and read both back
// through ts-go and after a dashboard restart.
func TestDatabaseRegionsPersistInTerraBase(t *testing.T) {
	if testing.Short() {
		t.Skip("starts pinned TerraBase and Chromium")
	}
	tb := terrabasetest.Start(t)
	db, key := terrabasetest.Database(t, tb, "dashboard")
	must(t, terrabase.EnsureCollections(t.Context(), db))
	must(t, terrabase.New(db).SeedPlans(t.Context()))
	cfg := config.Config{
		TerraBaseURL: tb.URL, TerraBaseAPIKey: key, TerraBaseProjectID: db.Project(), TerraBaseDatabase: db.Name(),
		TerraBaseHTTPClient: tb.Client, CSRFSecret: []byte(strings.Repeat("r", 32)), AtlasURL: atlasURL,
	}
	application, err := app.New(cfg)
	must(t, err)
	var active atomic.Pointer[app.Application]
	active.Store(application)
	dashboard := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		active.Load().Handler.ServeHTTP(w, r)
	}))
	t.Cleanup(dashboard.Close)
	page := watchedPage(t, newContext(t, dashboard.URL))
	account := newAccount("terrabase-regions")
	registerAndOnboard(t, page, account)

	goToOK(t, page, "/databases?create=1")
	dialog := pageByRole(page, *playwright.AriaRoleDialog, "New database")
	selectDatabaseRegions(t, dialog, "GRU", "SFO")
	fill(t, dialog.Locator("#database-name"), "replicated")
	submitAndExpect(t, page, "/api/v1/dashboard/databases", 303, byRole(dialog, *playwright.AriaRoleButton, "Create database").Click)
	waitForPath(t, page, func(u *url.URL) bool { return entityPath(u.Path, "/databases/") })
	must(t, expect.Locator(page.Locator("#placement")).ToContainText("Replicated across GRU, SFO"))
	databasePath := currentPath(t, page)
	stored := func() []string {
		t.Helper()
		a, err := terrabase.New(db).FindByEmail(t.Context(), account.Email)
		must(t, err)
		for _, org := range a.Organizations {
			for _, project := range org.Projects {
				for _, database := range project.Databases {
					if database.Name == "replicated" {
						return database.Regions
					}
				}
			}
		}
		t.Fatal("database missing from TerraBase")
		return nil
	}
	if got := stored(); !slices.Equal(got, []string{"GRU", "SFO"}) {
		t.Fatalf("stored regions = %v, want GRU and SFO", got)
	}

	form := page.Locator(`#placement form[action="/api/v1/dashboard/databases/regions"]`)
	must(t, form.Locator(`input[value="SFO"]`).Uncheck())
	submitAndExpect(t, page, "/api/v1/dashboard/databases/regions", 303, byRole(form, *playwright.AriaRoleButton, "Save regions").Click)
	if got := stored(); !slices.Equal(got, []string{"GRU"}) {
		t.Fatalf("stored regions = %v, want GRU", got)
	}

	application, err = app.New(cfg)
	must(t, err)
	active.Store(application)
	goToOK(t, page, databasePath)
	must(t, expect.Locator(page.Locator("#placement")).ToContainText("Single region · GRU only"))
}
