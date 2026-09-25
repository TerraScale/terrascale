package databases

import (
	"bytes"
	"context"
	json "encoding/json/v2"
	"net/http"
	"net/http/httptest"
	"net/url"
	"regexp"
	"strings"
	"testing"

	"terrascale.tech/dashboard/internal/core"
)

func TestDatabaseContentPreservesValidNamePattern(t *testing.T) {
	var out bytes.Buffer
	d := pageData{ProjectID: "project-1", Region: "GRU", CanProvision: true}
	if err := databasesContent(core.Page{CSRFToken: "test-csrf"}, d).Render(context.Background(), &out); err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(out.String(), `pattern="[a-z0-9][a-z0-9\-]*"`) {
		t.Fatalf("database name pattern missing from rendered form: %s", out.String())
	}
}

func TestDatabaseListShowsColoredSummary(t *testing.T) {
	summary := summarize(map[string]core.Database{
		"db-1": {ID: "db-1", Name: "orders", Status: "ready", Region: "GRU", Documents: map[string]map[string][]byte{"orders": {"a": nil, "b": nil}}},
		"db-2": {ID: "db-2", Name: "events", Status: "provisioning", Region: "FRA"},
		"db-3": {ID: "db-3", Name: "logs", Status: "failed", Region: "GRU"},
	})
	d := pageData{ProjectID: "project-1", Summary: summary, Databases: []databaseView{{ID: "db-1", Name: "orders", Status: "ready", Region: "GRU"}}}
	var out bytes.Buffer
	if err := databasesContent(core.Page{}, d).Render(context.Background(), &out); err != nil {
		t.Fatal(err)
	}
	for _, expected := range []string{
		`aria-label="Database totals"`,
		`<div class="ts-stat" data-tone="blue"><dt class="ts-stat-label"><span class="ts-dot" aria-hidden="true"></span>`,
		`Databases</dt><dd class="ts-stat-value">3</dd>`,
		`Ready</dt><dd class="ts-stat-value">1</dd><dd class="ts-stat-detail">33% of databases</dd>`,
		`Provisioning</dt><dd class="ts-stat-value">1</dd>`,
		`Failed</dt><dd class="ts-stat-value">1</dd>`,
		`Regions</dt><dd class="ts-stat-value">2</dd><dd class="ts-stat-detail">FRA · GRU</dd>`,
		`Documents</dt><dd class="ts-stat-value">2</dd><dd class="ts-stat-detail">Across 1 collection</dd>`,
		`<span class="ts-badge ts-badge-tone" data-tone="emerald"><span class="ts-dot" aria-hidden="true"></span>ready</span>`,
	} {
		if !strings.Contains(out.String(), expected) {
			t.Errorf("database list is missing %q", expected)
		}
	}

	out.Reset()
	if err := databasesContent(core.Page{}, pageData{ProjectID: "project-1"}).Render(context.Background(), &out); err != nil {
		t.Fatal(err)
	}
	if strings.Contains(out.String(), "Database totals") {
		t.Error("empty project renders database totals")
	}
}

func TestDatabaseContentHidesUnauthorizedMutations(t *testing.T) {
	d := pageData{ProjectID: "project-1", Region: "GRU", Detail: &databaseView{ID: "db-1", Name: "primary", Status: "ready"}}
	var out bytes.Buffer
	if err := databasesContent(core.Page{CSRFToken: "test-csrf"}, d).Render(context.Background(), &out); err != nil {
		t.Fatal(err)
	}
	if strings.Contains(out.String(), "Create branch") || strings.Contains(out.String(), "Delete database") || !strings.Contains(out.String(), "Data browser") {
		t.Fatalf("read-only database detail shows mutation controls or hides navigation: %s", out.String())
	}
	if !strings.Contains(out.String(), "/data?view=table&amp;databaseId=db-1") {
		t.Fatalf("data browser does not open table view: %s", out.String())
	}
}

type testSessions struct{ p core.Principal }

func (s testSessions) Start(http.ResponseWriter, *http.Request, core.Principal) error { return nil }
func (s testSessions) Principal(*http.Request) (core.Principal, bool)                 { return s.p, true }
func (s testSessions) BearerPrincipal(*http.Request) (core.Principal, bool)           { return s.p, false }
func (s testSessions) Token(*http.Request) (string, bool)                             { return "", false }
func (s testSessions) End(http.ResponseWriter, *http.Request)                         {}
func (s testSessions) RevokeUser(string, string)                                      {}

type testCSRF struct{}

func (testCSRF) Token(http.ResponseWriter, *http.Request) string { return "test-token" }
func (testCSRF) Verify(*http.Request) error                      { return nil }
func (testCSRF) Protect(h http.Handler) http.Handler             { return h }

type testRenderer struct{}

func (testRenderer) Page(http.ResponseWriter, *http.Request, core.Page) error {
	return nil
}
func (testRenderer) Fragment(http.ResponseWriter, *http.Request, core.Fragment) error {
	return nil
}

type renderingRenderer struct{ testRenderer }

func (renderingRenderer) Page(w http.ResponseWriter, r *http.Request, page core.Page) error {
	return page.View(page).Render(r.Context(), w)
}

func TestDatabaseListSearchKeepsDetailAccessible(t *testing.T) {
	deps, p := databaseFixture(t, "owner")
	if err := deps.Store.UpdateAs(p, func(a *core.Account) error {
		org := a.Organizations[p.OrganizationID]
		project := org.Projects[p.ProjectID]
		project.Databases["db-1"] = core.Database{ID: "db-1", ProjectID: p.ProjectID, Name: "orders"}
		project.Databases["db-2"] = core.Database{ID: "db-2", ProjectID: p.ProjectID, Name: "customers"}
		org.Projects[p.ProjectID] = project
		a.Organizations[p.OrganizationID] = org
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	deps.Render = renderingRenderer{}
	mux := http.NewServeMux()
	Mount(mux, deps)
	w := httptest.NewRecorder()
	mux.ServeHTTP(w, httptest.NewRequest(http.MethodGet, "/databases?q=ORD", nil))
	if w.Code != http.StatusOK || !strings.Contains(w.Body.String(), ">orders<") || strings.Contains(w.Body.String(), ">customers<") {
		t.Fatalf("database filter status %d: %s", w.Code, w.Body.String())
	}
	w = httptest.NewRecorder()
	mux.ServeHTTP(w, httptest.NewRequest(http.MethodGet, "/databases/db-2?q=ORD", nil))
	if w.Code != http.StatusOK || !strings.Contains(w.Body.String(), ">customers<") {
		t.Fatalf("filtered detail status %d: %s", w.Code, w.Body.String())
	}
}

func TestDatabaseCreateDisclosureStartsClosedAndOpensFromQuery(t *testing.T) {
	for _, tc := range []struct {
		role, path string
		wantOpen   bool
		wantForm   bool
	}{
		{"owner", "/databases", false, true},
		{"owner", "/databases?create=1", true, true},
		{"member", "/databases?create=1", false, false},
	} {
		t.Run(tc.role+tc.path, func(t *testing.T) {
			deps, _ := databaseFixture(t, tc.role)
			deps.Render = renderingRenderer{}
			mux := http.NewServeMux()
			Mount(mux, deps)
			w := httptest.NewRecorder()
			mux.ServeHTTP(w, httptest.NewRequest(http.MethodGet, tc.path, nil))
			opening := regexp.MustCompile(`<details[^>]*id="create-database"[^>]*>`).FindString(w.Body.String())
			if w.Code != http.StatusOK || (opening != "") != tc.wantForm || strings.Contains(opening, " open") != tc.wantOpen {
				t.Fatalf("database creation disclosure status %d, opening %q", w.Code, opening)
			}
			if tc.wantForm && (!strings.Contains(w.Body.String(), `action="/api/v1/dashboard/databases"`) || !strings.Contains(w.Body.String(), `name="_csrf" value="test-token"`) || !strings.Contains(w.Body.String(), "No databases yet.")) {
				t.Fatalf("creation form lost native POST, CSRF, or visible list: %s", w.Body.String())
			}
		})
	}
}

func databaseFixture(t *testing.T, role string) (core.Deps, core.Principal) {
	t.Helper()
	p := core.Principal{AccountID: "acct-1", UserID: "user-1", OrganizationID: "org-1", ProjectID: "project-1", Role: role}
	store := core.NewStore()
	if err := store.InsertAccount(core.Account{ID: p.AccountID, User: core.User{ID: p.UserID, Email: "tester@example.test", Name: "Tester", Role: role, PasswordHash: "test-hash"}, ActiveOrganizationID: p.OrganizationID, ActiveProjectID: p.ProjectID, Organizations: map[string]core.Organization{
		p.OrganizationID: {ID: p.OrganizationID, Kind: "business", Status: "active", BillingStatus: "active", Members: map[string]core.Member{p.UserID: {UserID: p.UserID, Role: role, Status: "active"}}, Projects: map[string]core.Project{
			p.ProjectID: {ID: p.ProjectID, OrganizationID: p.OrganizationID, Name: "Project", Region: "GRU", Databases: make(map[string]core.Database)},
		}},
	}}); err != nil {
		t.Fatal(err)
	}
	return core.Deps{Store: store, Sessions: testSessions{p}, CSRF: testCSRF{}, Render: testRenderer{}, Demo: true}, p
}

func post(mux *http.ServeMux, path string, values url.Values) *httptest.ResponseRecorder {
	r := httptest.NewRequest(http.MethodPost, path, strings.NewReader(values.Encode()))
	r.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	w := httptest.NewRecorder()
	mux.ServeHTTP(w, r)
	return w
}

func TestDemoDatabaseLifecycleAndBranchSnapshot(t *testing.T) {
	deps, p := databaseFixture(t, "owner")
	mux := http.NewServeMux()
	Mount(mux, deps)
	created := post(mux, "/api/v1/dashboard/databases", url.Values{"name": {"primary"}, "region": {"GRU"}})
	if created.Code != http.StatusCreated {
		t.Fatalf("create database: %d %s", created.Code, created.Body.String())
	}
	var source databaseView
	if err := json.Unmarshal(created.Body.Bytes(), &source); err != nil {
		t.Fatal(err)
	}
	if got := post(mux, "/api/v1/dashboard/databases", url.Values{"name": {"primary"}, "region": {"GRU"}}).Code; got != http.StatusConflict {
		t.Fatalf("duplicate create status = %d, want 409", got)
	}
	if err := deps.Store.UpdateAs(p, func(a *core.Account) error {
		org := a.Organizations[p.OrganizationID]
		project := org.Projects[p.ProjectID]
		db := project.Databases[source.ID]
		db.Documents = map[string]map[string][]byte{"users": {"ada": []byte(`{"name":"Ada"}`)}}
		project.Databases[source.ID] = db
		org.Projects[p.ProjectID] = project
		a.Organizations[p.OrganizationID] = org
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	branched := post(mux, "/api/v1/dashboard/databases/branch", url.Values{"databaseId": {source.ID}, "name": {"branch"}})
	if branched.Code != http.StatusCreated {
		t.Fatalf("branch database: %d %s", branched.Code, branched.Body.String())
	}
	var branch databaseView
	if err := json.Unmarshal(branched.Body.Bytes(), &branch); err != nil {
		t.Fatal(err)
	}
	a, err := deps.Store.SnapshotAs(p)
	if err != nil {
		t.Fatal(err)
	}
	if got := string(a.Organizations[p.OrganizationID].Projects[p.ProjectID].Databases[branch.ID].Documents["users"]["ada"]); got != `{"name":"Ada"}` {
		t.Fatalf("branch document = %s", got)
	}
	if got := post(mux, "/api/v1/dashboard/databases/delete", url.Values{"databaseId": {source.ID}, "confirmName": {"wrong"}}).Code; got != http.StatusBadRequest {
		t.Fatalf("wrong confirmation status = %d, want 400", got)
	}
	if got := post(mux, "/api/v1/dashboard/databases/delete", url.Values{"databaseId": {source.ID}, "confirmName": {"primary"}}).Code; got != http.StatusNoContent {
		t.Fatalf("delete status = %d, want 204", got)
	}
}

func TestMemberCannotCreateDatabase(t *testing.T) {
	deps, _ := databaseFixture(t, "member")
	mux := http.NewServeMux()
	Mount(mux, deps)
	if got := post(mux, "/api/v1/dashboard/databases", url.Values{"name": {"denied"}, "region": {"GRU"}}).Code; got != http.StatusForbidden {
		t.Fatalf("member create status = %d, want 403", got)
	}
}
