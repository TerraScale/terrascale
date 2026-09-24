package data

import (
	"io/fs"
	"net/http"
	"net/http/httptest"
	"net/url"
	"strings"
	"testing"

	"terrascale.tech/dashboard/internal/core"
)

type testSessions struct{ principal core.Principal }

func (s testSessions) Start(http.ResponseWriter, *http.Request, core.Principal) error { return nil }
func (s testSessions) Principal(*http.Request) (core.Principal, bool) {
	return s.principal, true
}
func (s testSessions) BearerPrincipal(*http.Request) (core.Principal, bool) {
	return s.principal, false
}
func (s testSessions) Token(*http.Request) (string, bool)     { return "", false }
func (s testSessions) End(http.ResponseWriter, *http.Request) {}
func (s testSessions) RevokeUser(string, string)              {}

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

func dataFixture(t *testing.T, role string) (core.Deps, core.Principal) {
	t.Helper()
	p := core.Principal{AccountID: "acct-1", UserID: "user-1", OrganizationID: "org-1", ProjectID: "project-1", Role: role}
	store := core.NewStore()
	if err := store.InsertAccount(core.Account{ID: p.AccountID, User: core.User{ID: p.UserID, Email: "tester@example.test", Name: "Tester", Role: role, PasswordHash: "test-hash"}, ActiveOrganizationID: p.OrganizationID, ActiveProjectID: p.ProjectID, Organizations: map[string]core.Organization{
		p.OrganizationID: {ID: p.OrganizationID, Kind: "business", Status: "active", BillingStatus: "active", Members: map[string]core.Member{p.UserID: {UserID: p.UserID, Role: role, Status: "active"}}, Projects: map[string]core.Project{
			"project-1": {ID: "project-1", OrganizationID: p.OrganizationID, Databases: map[string]core.Database{"db-1": {ID: "db-1", ProjectID: "project-1", Name: "first", Status: "ready"}}},
			"project-2": {ID: "project-2", OrganizationID: p.OrganizationID, Databases: map[string]core.Database{"db-2": {ID: "db-2", ProjectID: "project-2", Name: "second", Status: "ready"}}},
		}},
	}}); err != nil {
		t.Fatal(err)
	}
	return core.Deps{Store: store, Sessions: testSessions{p}, CSRF: testCSRF{}, Render: testRenderer{}, Demo: true}, p
}

func postDocument(mux *http.ServeMux, db, id string) *httptest.ResponseRecorder {
	form := url.Values{"databaseId": {db}, "collection": {"users"}, "operation": {"create"}, "id": {id}, "document": {`{"value":9007199254740993}`}}
	r := httptest.NewRequest(http.MethodPost, "/api/v1/dashboard/data/documents", strings.NewReader(form.Encode()))
	r.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	w := httptest.NewRecorder()
	mux.ServeHTTP(w, r)
	return w
}

func TestDocumentMutationRejectsGuessedProjectDatabase(t *testing.T) {
	deps, p := dataFixture(t, "member")
	mux := http.NewServeMux()
	Mount(mux, deps)
	if got := postDocument(mux, "db-2", "foreign").Code; got != http.StatusNotFound {
		t.Fatalf("guessed database status = %d, want 404", got)
	}
	if got := postDocument(mux, "db-1", "own").Code; got != http.StatusOK {
		t.Fatalf("own database status = %d, want 200", got)
	}
	a, err := deps.Store.SnapshotAs(p)
	if err != nil {
		t.Fatal(err)
	}
	first := a.Organizations[p.OrganizationID].Projects["project-1"].Databases["db-1"]
	second := a.Organizations[p.OrganizationID].Projects["project-2"].Databases["db-2"]
	if got := string(first.Documents["users"]["own"]); got != `{"value":9007199254740993}` {
		t.Fatalf("own document = %s", got)
	}
	if len(second.Documents) != 0 {
		t.Fatal("guessed database was mutated")
	}
}

func TestViewerCanReadButCannotWrite(t *testing.T) {
	deps, _ := dataFixture(t, "viewer")
	mux := http.NewServeMux()
	Mount(mux, deps)
	if got := postDocument(mux, "db-1", "denied").Code; got != http.StatusForbidden {
		t.Fatalf("viewer write status = %d, want 403", got)
	}
	r := httptest.NewRequest(http.MethodGet, "/api/v1/dashboard/data/documents?databaseId=db-1&collection=users", nil)
	w := httptest.NewRecorder()
	mux.ServeHTTP(w, r)
	if w.Code != http.StatusOK {
		t.Fatalf("viewer read status = %d, want 200", w.Code)
	}
}

func TestMergePatchPreservesLargeJSONNumber(t *testing.T) {
	merged, err := mergePatch([]byte(`{"count":9007199254740993,"profile":{"name":"Ada"}}`), []byte(`{"profile":{"name":"Grace"}}`))
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(string(merged), "9007199254740993") || !strings.Contains(string(merged), `"Grace"`) {
		t.Fatalf("merge lost precision or nested change: %s", merged)
	}
}
