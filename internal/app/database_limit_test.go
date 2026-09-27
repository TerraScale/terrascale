package app

import (
	json "encoding/json/v2"
	"net/http"
	"net/http/httptest"
	"net/url"
	"strings"
	"testing"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/features/backups"
	"terrascale.tech/dashboard/internal/features/databases"
	"terrascale.tech/dashboard/internal/features/settings"
	"terrascale.tech/dashboard/internal/platform/csrf"
	"terrascale.tech/dashboard/internal/platform/session"
)

// databaseRoutes serves every route that adds a database, signed in as the
// owner of a new Free organization.
type databaseRoutes struct {
	t       *testing.T
	handler http.Handler
	cookies []*http.Cookie
	token   string
}

func newDatabaseRoutes(t *testing.T) databaseRoutes {
	t.Helper()
	store := core.NewStore()
	account, err := store.CreateAccount(core.User{Name: "Owner", Email: "limit-owner@example.test", PasswordHash: "unused"})
	if err != nil {
		t.Fatal(err)
	}
	sessions := session.New(store, session.Options{})
	guard := csrf.New(sessions, csrf.Options{})
	deps := core.Deps{Store: store, Sessions: sessions, CSRF: guard, Tools: core.NewTools(store), Demo: true}
	mux := http.NewServeMux()
	databases.Mount(mux, deps)
	backups.Mount(mux, deps)
	settings.Mount(mux, deps)

	signIn := httptest.NewRecorder()
	request := httptest.NewRequest(http.MethodGet, "/", nil)
	if err := sessions.Start(signIn, request, account.Principal()); err != nil {
		t.Fatal(err)
	}
	token := guard.Token(signIn, request)
	return databaseRoutes{t: t, handler: guard.Protect(mux), cookies: signIn.Result().Cookies(), token: token}
}

func (routes databaseRoutes) serve(method, path string, form url.Values) (int, string) {
	routes.t.Helper()
	request := httptest.NewRequest(method, path, strings.NewReader(form.Encode()))
	request.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	request.Header.Set("Accept", "application/json")
	request.Header.Set("X-CSRF-Token", routes.token)
	for _, cookie := range routes.cookies {
		request.AddCookie(cookie)
	}
	response := httptest.NewRecorder()
	routes.handler.ServeHTTP(response, request)
	return response.Code, strings.TrimSpace(response.Body.String())
}

func (routes databaseRoutes) create(path string, form url.Values) string {
	routes.t.Helper()
	status, body := routes.serve(http.MethodPost, path, form)
	var created struct {
		ID string `json:"id"`
	}
	if status != http.StatusCreated || json.Unmarshal([]byte(body), &created) != nil || created.ID == "" {
		routes.t.Fatalf("POST %s = %d %s, want 201 with an id", path, status, body)
	}
	return created.ID
}

// TestDatabaseRoutesKeepThePlanDatabaseLimit fills a Free organization's one
// database and checks that no route adds another.
func TestDatabaseRoutesKeepThePlanDatabaseLimit(t *testing.T) {
	routes := newDatabaseRoutes(t)
	databaseID := routes.create("/api/v1/dashboard/databases", url.Values{"name": {"first"}, "region": {"GRU"}})
	backupID := routes.create("/api/v1/dashboard/backups", url.Values{"databaseId": {databaseID}})

	for _, attempt := range []struct {
		path string
		form url.Values
	}{
		{"/api/v1/dashboard/databases", url.Values{"name": {"second"}, "region": {"GRU"}}},
		{"/api/v1/dashboard/databases/branch", url.Values{"databaseId": {databaseID}, "name": {"first-branch"}}},
		{"/api/v1/dashboard/backups/restore", url.Values{"databaseId": {databaseID}, "backupId": {backupID}, "name": {"first-restore"}}},
		{"/api/v1/dashboard/settings/restore", url.Values{"databaseId": {databaseID}, "backupId": {backupID}}},
	} {
		status, body := routes.serve(http.MethodPost, attempt.path, attempt.form)
		if status != http.StatusConflict || !strings.Contains(body, "database limit") {
			t.Errorf("POST %s at the limit = %d %q, want 409 naming the database limit", attempt.path, status, body)
		}
	}

	_, body := routes.serve(http.MethodGet, "/api/v1/dashboard/databases", nil)
	var listed []struct {
		ID string `json:"id"`
	}
	if err := json.Unmarshal([]byte(body), &listed); err != nil || len(listed) != 1 || listed[0].ID != databaseID {
		t.Fatalf("databases = %s, want only %s", body, databaseID)
	}
}

func TestBackupRestoreUsesTheDatabaseNameRule(t *testing.T) {
	routes := newDatabaseRoutes(t)
	databaseID := routes.create("/api/v1/dashboard/databases", url.Values{"name": {"first"}, "region": {"GRU"}})
	backupID := routes.create("/api/v1/dashboard/backups", url.Values{"databaseId": {databaseID}})
	for _, name := range []string{"Restored_UPPER!", "first-", strings.Repeat("a", 64)} {
		status, _ := routes.serve(http.MethodPost, "/api/v1/dashboard/backups/restore", url.Values{
			"databaseId": {databaseID}, "backupId": {backupID}, "name": {name},
		})
		if status != http.StatusBadRequest {
			t.Errorf("restore as %q = %d, want 400", name, status)
		}
	}
}
