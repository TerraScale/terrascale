package admin

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

func TestAdminRoutesAndActionsUsePersistedAdminAuthorization(t *testing.T) {
	store := core.NewStore()
	adminAccount := newAdminAccount(t, store, "admin@example.test", true)
	member := newAdminAccount(t, store, "member@example.test", false)
	intruder := newAdminAccount(t, store, "intruder@example.test", false)
	seedAdminDatabase(t, store, member, "Tenant database", "GRU")

	principal := adminAccount.Principal()
	principal.PlatformAdmin = false // The store remains the source of authority.
	mux := adminTestMux(t, store, principal, adminTestCSRF{valid: true})

	root := httptest.NewRecorder()
	mux.ServeHTTP(root, httptest.NewRequest(http.MethodGet, "/admin", nil))
	if root.Code != http.StatusFound || root.Header().Get("Location") != "/admin/users" {
		t.Fatalf("GET /admin = (%d, %q), want redirect to /admin/users", root.Code, root.Header().Get("Location"))
	}

	users := httptest.NewRecorder()
	mux.ServeHTTP(users, httptest.NewRequest(http.MethodGet, "/admin/users", nil))
	if users.Code != http.StatusOK || !strings.Contains(users.Body.String(), member.User.Email) || !strings.Contains(users.Body.String(), "name=\"_csrf\"") {
		t.Fatalf("GET /admin/users status/body = %d/%s", users.Code, users.Body.String())
	}
	for path, expected := range map[string]string{
		"/admin/organizations": "Personal",
		"/admin/projects":      "GRU",
		"/admin/plans":         "Starter",
	} {
		page := httptest.NewRecorder()
		mux.ServeHTTP(page, httptest.NewRequest(http.MethodGet, path, nil))
		if page.Code != http.StatusOK || !strings.Contains(page.Body.String(), expected) {
			t.Fatalf("GET %s = %d, want 200 with %q; body=%s", path, page.Code, expected, page.Body.String())
		}
		if path == "/admin/plans" {
			body := page.Body.String()
			if !strings.Contains(body, `name="key" maxlength="64" pattern="[a-z0-9][a-z0-9_-]*" required`) {
				t.Fatal("new plan key is not editable")
			}
			if !strings.Contains(body, `name="key" maxlength="64" value="starter" pattern="[a-z0-9][a-z0-9_-]*" required readonly`) {
				t.Fatal("existing plan key is not read-only and submitted")
			}
		}
	}
	for path, expected := range map[string]string{
		"/admin/databases": "Tenant database",
		"/admin/estate":    "GRU",
		"/admin/usage":     "Provider-metered usage is unavailable.",
	} {
		page := httptest.NewRecorder()
		mux.ServeHTTP(page, httptest.NewRequest(http.MethodGet, path, nil))
		if page.Code != http.StatusOK || !strings.Contains(page.Body.String(), expected) {
			t.Fatalf("GET %s = %d, want 200 with %q; body=%s", path, page.Code, expected, page.Body.String())
		}
	}
	databaseSearch := httptest.NewRecorder()
	mux.ServeHTTP(databaseSearch, httptest.NewRequest(http.MethodGet, "/admin/databases?q=Tenant", nil))
	if databaseSearch.Code != http.StatusOK || !strings.Contains(databaseSearch.Body.String(), "Tenant database") ||
		!strings.Contains(databaseSearch.Body.String(), "Version details are unavailable here.") {
		t.Fatalf("database inventory search did not show unavailable version details: status=%d body=%s", databaseSearch.Code, databaseSearch.Body.String())
	}

	org := member.Organizations[member.ActiveOrganizationID]
	project := org.Projects[member.ActiveProjectID]
	response := adminPost(t, mux, "/api/v1/admin/users/"+member.ID, url.Values{
		"name": {"Updated Member"}, "platformAdmin": {"true"},
	})
	if response.Code != http.StatusSeeOther {
		t.Fatalf("update user status = %d, body=%s", response.Code, response.Body.String())
	}
	response = adminPost(t, mux, "/api/v1/admin/organizations/"+member.ID+"/"+org.ID, url.Values{
		"name": {"Customer Org"},
	})
	if response.Code != http.StatusSeeOther {
		t.Fatalf("rename organization status = %d, body=%s", response.Code, response.Body.String())
	}
	response = adminPost(t, mux, "/api/v1/admin/projects/"+member.ID+"/"+org.ID+"/"+project.ID, url.Values{
		"name": {"Customer Project"},
	})
	if response.Code != http.StatusSeeOther {
		t.Fatalf("rename project status = %d, body=%s", response.Code, response.Body.String())
	}
	response = adminPost(t, mux, "/api/v1/admin/plans", url.Values{
		"key": {"team"}, "name": {"Team"}, "priceCents": {"1500"}, "annualPriceCents": {"15000"},
		"trialDays": {"14"}, "storageMiB": {"1024"}, "monthlyRequests": {"50000"}, "visible": {"true"},
	})
	if response.Code != http.StatusSeeOther {
		t.Fatalf("create plan status = %d, body=%s", response.Code, response.Body.String())
	}
	team, ok := store.Plan("team")
	if !ok || team.PriceCents != 1500 || !team.Visible {
		t.Fatalf("created plan = (%+v, %t)", team, ok)
	}
	response = adminPost(t, mux, "/api/v1/admin/plans/"+team.ID+"/visibility", url.Values{"visible": {"false"}})
	if response.Code != http.StatusSeeOther {
		t.Fatalf("change plan visibility status = %d, body=%s", response.Code, response.Body.String())
	}
	team, _ = store.Plan(team.ID)
	if team.Visible {
		t.Fatal("plan visibility remained enabled")
	}
	response = adminPost(t, mux, "/api/v1/admin/plans/"+team.ID+"/delete", url.Values{})
	if response.Code != http.StatusSeeOther {
		t.Fatalf("delete plan status = %d, body=%s", response.Code, response.Body.String())
	}
	if _, ok := store.Plan("team"); ok {
		t.Fatal("deleted plan remains in the catalog")
	}

	updated, ok := store.Snapshot(member.ID)
	if !ok {
		t.Fatal("updated account is missing")
	}
	updatedOrg := updated.Organizations[org.ID]
	updatedProject := updatedOrg.Projects[project.ID]
	if updated.User.Name != "Updated Member" || !updated.User.PlatformAdmin || updatedOrg.Name != "Customer Org" || updatedProject.Name != "Customer Project" {
		t.Fatalf("admin changes were not persisted: account=%+v organization=%+v project=%+v", updated.User, updatedOrg, updatedProject)
	}

	serviceErr := NewService(store).UpdateUser(intruder.Principal(), adminAccount.ID, "Should fail", false)
	if !errors.Is(serviceErr, core.ErrForbidden) {
		t.Fatalf("non-admin service update error = %v, want forbidden", serviceErr)
	}
}

func TestAdminRouteRejectsForgedPrincipalAndInvalidCSRF(t *testing.T) {
	store := core.NewStore()
	adminAccount := newAdminAccount(t, store, "admin@example.test", true)
	member := newAdminAccount(t, store, "member@example.test", false)

	forged := member.Principal()
	forged.PlatformAdmin = true
	mux := adminTestMux(t, store, forged, adminTestCSRF{valid: true})
	response := httptest.NewRecorder()
	mux.ServeHTTP(response, httptest.NewRequest(http.MethodGet, "/admin/users", nil))
	if response.Code != http.StatusForbidden {
		t.Fatalf("forged admin page status = %d, want 403", response.Code)
	}
	for _, path := range []string{"/admin/databases", "/admin/estate", "/admin/usage"} {
		response := httptest.NewRecorder()
		mux.ServeHTTP(response, httptest.NewRequest(http.MethodGet, path, nil))
		if response.Code != http.StatusForbidden {
			t.Fatalf("forged admin route %s status = %d, want 403", path, response.Code)
		}
	}
	if err := NewService(store).RenameOrganization(forged, adminAccount.ID, adminAccount.ActiveOrganizationID, "Nope"); !errors.Is(err, core.ErrForbidden) {
		t.Fatalf("direct service mutation error = %v, want forbidden", err)
	}

	csrfMux := adminTestMux(t, store, adminAccount.Principal(), adminTestCSRF{valid: false})
	org := member.Organizations[member.ActiveOrganizationID]
	response = adminPost(t, csrfMux, "/api/v1/admin/organizations/"+member.ID+"/"+org.ID, url.Values{"name": {"Blocked"}})
	if response.Code != http.StatusForbidden {
		t.Fatalf("invalid CSRF status = %d, want 403", response.Code)
	}
	updated, _ := store.Snapshot(member.ID)
	if updated.Organizations[org.ID].Name != org.Name {
		t.Fatal("organization changed after invalid CSRF")
	}
}

func seedAdminDatabase(t *testing.T, store *core.Store, account core.Account, name, region string) {
	t.Helper()
	err := store.Update(account.ID, func(current *core.Account) error {
		org, ok := current.Organizations[current.ActiveOrganizationID]
		if !ok {
			return core.ErrNotFound
		}
		project, ok := org.Projects[current.ActiveProjectID]
		if !ok {
			return core.ErrNotFound
		}
		project.Databases["db-admin-fixture"] = core.Database{
			ID: "db-admin-fixture", ProjectID: project.ID, Name: name, Status: "ready", Region: region,
		}
		org.Projects[project.ID] = project
		current.Organizations[org.ID] = org
		return nil
	})
	if err != nil {
		t.Fatal(err)
	}
}

func TestUsageInventoryCountsCanonicalRecordsOnce(t *testing.T) {
	database := core.Database{ID: "database-1"}
	project := core.Project{
		ID: "project-1", Databases: map[string]core.Database{database.ID: database},
	}
	organization := core.Organization{
		ID: "organization-1", Projects: map[string]core.Project{project.ID: project},
	}
	accounts := []core.Account{
		{ID: "account-1", Organizations: map[string]core.Organization{organization.ID: organization}},
		{ID: "account-2", Organizations: map[string]core.Organization{organization.ID: organization}},
	}

	got := summarizeInventory(accounts)
	want := inventoryCounts{Accounts: 2, Organizations: 1, Projects: 1, Databases: 1}
	if got != want {
		t.Fatalf("canonical inventory = %+v, want %+v", got, want)
	}
}

func TestLastPlatformAdminCannotDemoteThemself(t *testing.T) {
	store := core.NewStore()
	account := newAdminAccount(t, store, "only-admin@example.test", true)
	mux := adminTestMux(t, store, account.Principal(), adminTestCSRF{valid: true})
	response := adminPost(t, mux, "/api/v1/admin/users/"+account.ID, url.Values{
		"name": {account.User.Name}, "platformAdmin": {"false"},
	})
	if response.Code != http.StatusSeeOther || !strings.Contains(response.Header().Get("Location"), "error=") {
		t.Fatalf("demote last admin response = (%d, %q), want an error redirect", response.Code, response.Header().Get("Location"))
	}
	updated, _ := store.Snapshot(account.ID)
	if !updated.User.PlatformAdmin {
		t.Fatal("last platform admin was demoted")
	}
}

func newAdminAccount(t *testing.T, store *core.Store, email string, platformAdmin bool) core.Account {
	t.Helper()
	account, err := store.CreateAccount(core.User{
		Email: email, Name: "Admin test user", PasswordHash: "test password hash", PlatformAdmin: platformAdmin,
	})
	if err != nil {
		t.Fatal(err)
	}
	return account
}

func adminTestMux(t *testing.T, store *core.Store, principal core.Principal, csrf adminTestCSRF) *http.ServeMux {
	t.Helper()
	renderer := render.New(store, csrf)
	mux := http.NewServeMux()
	Mount(mux, core.Deps{
		Store: store, Sessions: adminTestSessions{principal: principal},
		CSRF: csrf, Render: renderer, Demo: true,
	})
	return mux
}

func adminPost(t *testing.T, mux http.Handler, path string, values url.Values) *httptest.ResponseRecorder {
	t.Helper()
	if values == nil {
		values = make(url.Values)
	}
	values.Set("_csrf", "admin-test-token")
	request := httptest.NewRequest(http.MethodPost, path, strings.NewReader(values.Encode()))
	request.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	response := httptest.NewRecorder()
	mux.ServeHTTP(response, request)
	return response
}

type adminTestSessions struct{ principal core.Principal }

func (s adminTestSessions) Start(http.ResponseWriter, *http.Request, core.Principal) error {
	return nil
}
func (s adminTestSessions) Principal(*http.Request) (core.Principal, bool) {
	return s.principal, true
}
func (s adminTestSessions) BearerPrincipal(*http.Request) (core.Principal, bool) {
	return core.Principal{}, false
}
func (adminTestSessions) End(http.ResponseWriter, *http.Request) {}
func (adminTestSessions) RevokeUser(string, string)              {}

type adminTestCSRF struct{ valid bool }

func (adminTestCSRF) Token(http.ResponseWriter, *http.Request) string { return "admin-test-token" }
func (csrf adminTestCSRF) Verify(r *http.Request) error {
	if !csrf.valid {
		return errors.New("invalid CSRF token")
	}
	if err := r.ParseForm(); err != nil {
		return err
	}
	if r.PostForm.Get("_csrf") != "admin-test-token" {
		return errors.New("invalid CSRF token")
	}
	return nil
}
func (adminTestCSRF) Protect(next http.Handler) http.Handler { return next }
