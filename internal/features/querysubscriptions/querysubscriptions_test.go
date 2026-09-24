package querysubscriptions

import (
	"errors"
	"net/http"
	"net/http/httptest"
	"net/url"
	"strings"
	"testing"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/platform/render"
)

type testSessions struct{ principal core.Principal }

func (s testSessions) Start(http.ResponseWriter, *http.Request, core.Principal) error { return nil }
func (s testSessions) Principal(*http.Request) (core.Principal, bool) {
	return s.principal, true
}
func (s testSessions) BearerPrincipal(*http.Request) (core.Principal, bool) {
	return s.principal, false
}
func (testSessions) End(http.ResponseWriter, *http.Request) {}
func (testSessions) RevokeUser(string, string)              {}

type anonymousSessions struct{ testSessions }

func (anonymousSessions) Principal(*http.Request) (core.Principal, bool) {
	return core.Principal{}, false
}

type testCSRF struct{}

func (testCSRF) Token(http.ResponseWriter, *http.Request) string { return "csrf-test" }
func (testCSRF) Verify(r *http.Request) error {
	if r.Header.Get("X-Test-CSRF") != "csrf-test" {
		return errors.New("missing test CSRF")
	}
	return nil
}
func (testCSRF) Protect(h http.Handler) http.Handler { return h }

func fixture(t *testing.T, role string) (core.Deps, core.Principal) {
	t.Helper()
	p := core.Principal{AccountID: "acct-1", UserID: "user-1", OrganizationID: "org-1", ProjectID: "project-1", Role: role}
	store := core.NewStore()
	project := func(id, dbID string) core.Project {
		return core.Project{ID: id, OrganizationID: p.OrganizationID, Databases: map[string]core.Database{dbID: {ID: dbID, ProjectID: id, Name: dbID, Status: "ready"}}}
	}
	account := core.Account{ID: p.AccountID, User: core.User{ID: p.UserID, Email: "tester@example.test", Name: "Tester", PasswordHash: "test-hash", Role: role}, ActiveOrganizationID: p.OrganizationID, ActiveProjectID: p.ProjectID, Organizations: map[string]core.Organization{
		p.OrganizationID: {ID: p.OrganizationID, Kind: "business", Status: "active", BillingStatus: "active", OwnerUserID: p.UserID, Members: map[string]core.Member{p.UserID: {UserID: p.UserID, Role: role, Status: "active"}}, Projects: map[string]core.Project{
			"project-1": project("project-1", "db-1"),
			"project-2": project("project-2", "db-2"),
		}},
	}}
	if err := store.InsertAccount(account); err != nil {
		t.Fatal(err)
	}
	csrf := testCSRF{}
	return core.Deps{Store: store, Sessions: testSessions{p}, CSRF: csrf, Render: render.New(store, csrf), Demo: true}, p
}

func post(mux *http.ServeMux, path string, form url.Values, csrf bool) *httptest.ResponseRecorder {
	r := httptest.NewRequest(http.MethodPost, path, strings.NewReader(form.Encode()))
	r.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	if csrf {
		r.Header.Set("X-Test-CSRF", "csrf-test")
	}
	w := httptest.NewRecorder()
	mux.ServeHTTP(w, r)
	return w
}

func createForm(databaseID, name, query string) url.Values {
	return url.Values{"databaseId": {databaseID}, "name": {name}, "query": {query}, "transport": {"http"}}
}

func TestAnonymousPageRedirectsToLoginWhileMutationRequiresAuthentication(t *testing.T) {
	deps, _ := fixture(t, "owner")
	deps.Sessions = anonymousSessions{}
	mux := http.NewServeMux()
	Mount(mux, deps)
	w := httptest.NewRecorder()
	mux.ServeHTTP(w, httptest.NewRequest(http.MethodGet, "/subscriptions?from=dashboard", nil))
	if w.Code != http.StatusSeeOther {
		t.Fatalf("anonymous page status = %d, want redirect", w.Code)
	}
	login, err := url.Parse(w.Header().Get("Location"))
	if err != nil || login.Path != "/login" || login.Query().Get("returnTo") != "/subscriptions?from=dashboard" {
		t.Fatalf("anonymous page login target = %q: %v", w.Header().Get("Location"), err)
	}
	w = httptest.NewRecorder()
	mux.ServeHTTP(w, httptest.NewRequest(http.MethodGet, "/subscriptions?x="+strings.Repeat("a", 3000), nil))
	login, err = url.Parse(w.Header().Get("Location"))
	if err != nil || login.Query().Get("returnTo") != "/subscriptions" {
		t.Fatalf("oversized return target = %q: %v", w.Header().Get("Location"), err)
	}
	if got := post(mux, "/api/v1/dashboard/subscriptions/attach", createForm("db-1", "orders_view", "SELECT * FROM orders"), true).Code; got != http.StatusUnauthorized {
		t.Fatalf("anonymous mutation status = %d, want 401", got)
	}
}

func TestProjectScopedDemoConfigurationLifecycle(t *testing.T) {
	deps, p := fixture(t, "owner")
	mux := http.NewServeMux()
	Mount(mux, deps)
	form := createForm("db-1", "orders_view", "SELECT * FROM orders LIMIT 20")
	if got := post(mux, "/api/v1/dashboard/subscriptions/attach", form, false).Code; got != http.StatusForbidden {
		t.Fatalf("missing CSRF status = %d", got)
	}
	if got := post(mux, "/api/v1/dashboard/subscriptions/attach", createForm("db-2", "foreign", "SELECT * FROM orders"), true).Code; got != http.StatusNotFound {
		t.Fatalf("cross-project database status = %d", got)
	}
	if got := post(mux, "/api/v1/dashboard/subscriptions/attach", createForm("db-1", "unsafe", "DELETE FROM orders"), true).Code; got != http.StatusBadRequest {
		t.Fatalf("write SQL status = %d", got)
	}
	if got := post(mux, "/api/v1/dashboard/subscriptions/attach", form, true).Code; got != http.StatusSeeOther {
		t.Fatalf("create status = %d", got)
	}
	account, err := deps.Store.SnapshotAs(p)
	if err != nil {
		t.Fatal(err)
	}
	project := account.Organizations[p.OrganizationID].Projects[p.ProjectID]
	if len(project.SharedResultConfigs) != 1 || len(account.Organizations[p.OrganizationID].Projects["project-2"].SharedResultConfigs) != 0 {
		t.Fatal("configuration escaped selected project")
	}
	var id string
	for key, config := range project.SharedResultConfigs {
		id = key
		if config.Status != "saved" || config.DatabaseID != "db-1" || config.Name != "orders_view" {
			t.Fatalf("unexpected saved config: %#v", config)
		}
	}
	if got := post(mux, "/api/v1/dashboard/subscriptions/attach", form, true).Code; got != http.StatusConflict {
		t.Fatalf("duplicate view status = %d", got)
	}
	other := p
	other.ProjectID = "project-2"
	otherDeps := deps
	otherDeps.Sessions = testSessions{other}
	otherMux := http.NewServeMux()
	Mount(otherMux, otherDeps)
	if got := post(otherMux, "/api/v1/dashboard/subscriptions/pause", url.Values{"configId": {id}}, true).Code; got != http.StatusNotFound {
		t.Fatalf("cross-project pause status = %d", got)
	}
	if got := post(mux, "/api/v1/dashboard/subscriptions/pause", url.Values{"configId": {id}}, true).Code; got != http.StatusSeeOther {
		t.Fatalf("pause status = %d", got)
	}
	account, _ = deps.Store.SnapshotAs(p)
	if got := account.Organizations[p.OrganizationID].Projects[p.ProjectID].SharedResultConfigs[id].Status; got != "paused" {
		t.Fatalf("paused status = %q", got)
	}
	if got := post(mux, "/api/v1/dashboard/subscriptions/detach", url.Values{"configId": {id}}, true).Code; got != http.StatusSeeOther {
		t.Fatalf("detach status = %d", got)
	}
	account, _ = deps.Store.SnapshotAs(p)
	if len(account.Organizations[p.OrganizationID].Projects[p.ProjectID].SharedResultConfigs) != 0 {
		t.Fatal("detached configuration remains")
	}
}

func TestViewerCannotCreateConfigurationAndPageDoesNotClaimLiveState(t *testing.T) {
	deps, _ := fixture(t, "viewer")
	mux := http.NewServeMux()
	Mount(mux, deps)
	if got := post(mux, "/api/v1/dashboard/subscriptions/attach", createForm("db-1", "orders_view", "SELECT * FROM orders"), true).Code; got != http.StatusForbidden {
		t.Fatalf("viewer create status = %d", got)
	}
	w := httptest.NewRecorder()
	mux.ServeHTTP(w, httptest.NewRequest(http.MethodGet, "/subscriptions", nil))
	if w.Code != http.StatusOK {
		t.Fatalf("GET status = %d: %s", w.Code, w.Body.String())
	}
	if !strings.Contains(w.Body.String(), "Demo configuration only") || strings.Contains(w.Body.String(), "41 · set") {
		t.Fatal("page did not present truthful demo state")
	}
}
