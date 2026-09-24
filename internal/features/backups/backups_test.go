package backups

import (
	json "encoding/json/v2"
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

func (testRenderer) Page(http.ResponseWriter, *http.Request, core.Page) error {
	return nil
}
func (testRenderer) Fragment(http.ResponseWriter, *http.Request, core.Fragment) error {
	return nil
}

func backupFixture(t *testing.T, role string) (core.Deps, core.Principal) {
	t.Helper()
	p := core.Principal{AccountID: "acct-1", UserID: "user-1", OrganizationID: "org-1", ProjectID: "project-1", Role: role}
	store := core.NewStore()
	if err := store.InsertAccount(core.Account{ID: p.AccountID, User: core.User{ID: p.UserID, Email: "tester@example.test", Name: "Tester", Role: role, PasswordHash: "test-hash"}, ActiveOrganizationID: p.OrganizationID, ActiveProjectID: p.ProjectID, Organizations: map[string]core.Organization{
		p.OrganizationID: {ID: p.OrganizationID, Kind: "business", Status: "active", BillingStatus: "active", Members: map[string]core.Member{p.UserID: {UserID: p.UserID, Role: role, Status: "active"}}, Projects: map[string]core.Project{
			p.ProjectID: {ID: p.ProjectID, OrganizationID: p.OrganizationID, Databases: map[string]core.Database{
				"db-1": {ID: "db-1", ProjectID: p.ProjectID, Name: "original", Status: "ready", Documents: map[string]map[string][]byte{"users": {"ada": []byte(`{"name":"Ada"}`)}}},
			}},
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

func TestDemoBackupRestoresSnapshotSideBySide(t *testing.T) {
	deps, p := backupFixture(t, "owner")
	mux := http.NewServeMux()
	Mount(mux, deps)
	created := post(mux, "/api/v1/dashboard/backups", url.Values{"databaseId": {"db-1"}})
	if created.Code != http.StatusCreated {
		t.Fatalf("create backup: %d %s", created.Code, created.Body.String())
	}
	var backup backupView
	if err := json.Unmarshal(created.Body.Bytes(), &backup); err != nil {
		t.Fatal(err)
	}
	if err := deps.Store.UpdateAs(p, func(a *core.Account) error {
		org := a.Organizations[p.OrganizationID]
		project := org.Projects[p.ProjectID]
		db := project.Databases["db-1"]
		db.Documents["users"]["ada"] = []byte(`{"name":"Grace"}`)
		project.Databases["db-1"] = db
		org.Projects[p.ProjectID] = project
		a.Organizations[p.OrganizationID] = org
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	restored := post(mux, "/api/v1/dashboard/backups/restore", url.Values{"databaseId": {"db-1"}, "backupId": {backup.ID}, "name": {"restored"}})
	if restored.Code != http.StatusCreated {
		t.Fatalf("restore backup: %d %s", restored.Code, restored.Body.String())
	}
	var result struct {
		ID string `json:"id"`
	}
	if err := json.Unmarshal(restored.Body.Bytes(), &result); err != nil {
		t.Fatal(err)
	}
	a, err := deps.Store.SnapshotAs(p)
	if err != nil {
		t.Fatal(err)
	}
	project := a.Organizations[p.OrganizationID].Projects[p.ProjectID]
	if got := string(project.Databases[result.ID].Documents["users"]["ada"]); got != `{"name":"Ada"}` {
		t.Fatalf("restored snapshot = %s, want original Ada", got)
	}
	if got := string(project.Databases["db-1"].Documents["users"]["ada"]); got != `{"name":"Grace"}` {
		t.Fatalf("source was changed by restore: %s", got)
	}
}

func TestMemberCannotCreateBackup(t *testing.T) {
	deps, _ := backupFixture(t, "member")
	mux := http.NewServeMux()
	Mount(mux, deps)
	if got := post(mux, "/api/v1/dashboard/backups", url.Values{"databaseId": {"db-1"}}).Code; got != http.StatusForbidden {
		t.Fatalf("member backup status = %d, want 403", got)
	}
}
