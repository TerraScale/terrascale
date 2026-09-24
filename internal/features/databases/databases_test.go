package databases

import (
	json "encoding/json/v2"
	"io/fs"
	"net/http"
	"net/http/httptest"
	"net/url"
	"strings"
	"testing"

	"terrascale.tech/dashboard/internal/core"
)

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

func (testRenderer) Register(string, fs.FS, ...string) error { return nil }
func (testRenderer) Page(http.ResponseWriter, *http.Request, core.Page) error {
	return nil
}
func (testRenderer) Fragment(http.ResponseWriter, *http.Request, core.Fragment) error {
	return nil
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
