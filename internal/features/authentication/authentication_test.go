package authentication

import (
	"errors"
	"io/fs"
	"net/http"
	"net/http/httptest"
	"net/url"
	"strings"
	"testing"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/platform/render"
	"terrascale.tech/dashboard/internal/ui"
)

func TestAuthenticationStateIsSharedByProjectMembersAndIsolatedAcrossProjects(t *testing.T) {
	store, owner, admin, viewer, otherProjectID := sharedAuthAccounts(t)
	ownerPrincipal := owner.Principal()
	ownerMux := authTestMux(t, store, ownerPrincipal)
	form := url.Values{
		"name": {"Workforce"}, "protocol": {"OIDC"}, "issuer": {"https://identity.example.test"},
		"client_id": {"demo-client"}, "client_secret": {"do-not-persist-this-secret"},
	}
	request := httptest.NewRequest(http.MethodPost, "/api/v1/auth-management/connections", strings.NewReader(form.Encode()))
	request.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	response := httptest.NewRecorder()
	ownerMux.ServeHTTP(response, request)
	if response.Code != http.StatusSeeOther {
		t.Fatalf("owner save returned %d: %s", response.Code, response.Body.String())
	}

	updated, found := store.Snapshot(owner.ID)
	if !found {
		t.Fatal("owner account disappeared")
	}
	sharedOrg := updated.Organizations[ownerPrincipal.OrganizationID]
	instance := sharedOrg.Projects[ownerPrincipal.ProjectID].AuthInstance
	if len(instance.Resources) != 1 {
		t.Fatalf("stored %d auth resources, want one", len(instance.Resources))
	}
	resource := instance.Resources[0]
	if resource.Section != "connections" || resource.Values["credential_configured"] != "true" {
		t.Fatalf("connection demo state missing: %+v", resource)
	}
	if _, stored := resource.Values["client_secret"]; stored {
		t.Fatal("submitted client secret was retained in account state")
	}
	if len(updated.AuditEvents) != 1 || updated.AuditEvents[0].Action != "auth.connections.created" {
		t.Fatalf("authentication change was not audited: %+v", updated.AuditEvents)
	}

	adminPrincipal := sharedPrincipal(admin, ownerPrincipal.OrganizationID, ownerPrincipal.ProjectID, "admin")
	adminMux := authTestMux(t, store, adminPrincipal)
	adminPage := httptest.NewRecorder()
	adminMux.ServeHTTP(adminPage, httptest.NewRequest(http.MethodGet, "/auth/connections", nil))
	if adminPage.Code != http.StatusOK || !strings.Contains(adminPage.Body.String(), "Workforce") {
		t.Fatalf("admin could not read shared project auth state: status=%d body=%s", adminPage.Code, adminPage.Body.String())
	}
	if strings.Contains(adminPage.Body.String(), "do-not-persist-this-secret") {
		t.Fatal("shared connection page exposed a submitted client secret")
	}
	adminUpdate := url.Values{
		"id": {resource.ID}, "name": {"Workforce updated by admin"}, "protocol": {"OIDC"},
		"issuer": {"https://identity.example.test"}, "client_id": {"demo-client"},
	}
	request = httptest.NewRequest(http.MethodPost, "/api/v1/auth-management/connections", strings.NewReader(adminUpdate.Encode()))
	request.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	response = httptest.NewRecorder()
	adminMux.ServeHTTP(response, request)
	if response.Code != http.StatusSeeOther {
		t.Fatalf("admin shared update returned %d: %s", response.Code, response.Body.String())
	}
	adminAuditPage := httptest.NewRecorder()
	adminMux.ServeHTTP(adminAuditPage, httptest.NewRequest(http.MethodGet, "/auth/audit", nil))
	for _, expected := range []string{"auth.connections.created", "owner@example.test", "auth.connections.updated", "admin@example.test"} {
		if adminAuditPage.Code != http.StatusOK || !strings.Contains(adminAuditPage.Body.String(), expected) {
			t.Fatalf("admin audit page missing shared event %q: status=%d body=%s", expected, adminAuditPage.Code, adminAuditPage.Body.String())
		}
	}

	ownerPage := httptest.NewRecorder()
	ownerMux.ServeHTTP(ownerPage, httptest.NewRequest(http.MethodGet, "/auth/connections", nil))
	if ownerPage.Code != http.StatusOK || !strings.Contains(ownerPage.Body.String(), "Workforce updated by admin") {
		t.Fatalf("owner did not observe admin's project update: status=%d body=%s", ownerPage.Code, ownerPage.Body.String())
	}
	if strings.Contains(ownerPage.Body.String(), "do-not-persist-this-secret") {
		t.Fatal("owner page exposed a submitted client secret")
	}

	viewerPrincipal := sharedPrincipal(viewer, ownerPrincipal.OrganizationID, ownerPrincipal.ProjectID, "owner")
	viewerMux := authTestMux(t, store, viewerPrincipal)
	viewerPage := httptest.NewRecorder()
	viewerMux.ServeHTTP(viewerPage, httptest.NewRequest(http.MethodGet, "/auth/connections", nil))
	if viewerPage.Code != http.StatusOK || !strings.Contains(viewerPage.Body.String(), "Workforce updated by admin") {
		t.Fatalf("viewer could not read shared project auth state: status=%d body=%s", viewerPage.Code, viewerPage.Body.String())
	}
	viewerAuditPage := httptest.NewRecorder()
	viewerMux.ServeHTTP(viewerAuditPage, httptest.NewRequest(http.MethodGet, "/auth/audit", nil))
	for _, expected := range []string{"auth.connections.created", "owner@example.test", "auth.connections.updated", "admin@example.test"} {
		if viewerAuditPage.Code != http.StatusOK || !strings.Contains(viewerAuditPage.Body.String(), expected) {
			t.Fatalf("viewer audit page missing shared event %q: status=%d body=%s", expected, viewerAuditPage.Code, viewerAuditPage.Body.String())
		}
	}
	request = httptest.NewRequest(http.MethodPost, "/api/v1/auth-management/users", strings.NewReader(url.Values{"name": {"Intruder"}, "email": {"intruder@example.test"}}.Encode()))
	request.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	response = httptest.NewRecorder()
	viewerMux.ServeHTTP(response, request)
	if response.Code != http.StatusForbidden {
		t.Fatalf("viewer with stale admin principal role returned %d, want forbidden", response.Code)
	}
	viewerSnapshot, err := store.SnapshotAs(viewerPrincipal)
	if err != nil {
		t.Fatal(err)
	}
	viewerProject := viewerSnapshot.Organizations[ownerPrincipal.OrganizationID].Projects[ownerPrincipal.ProjectID]
	if len(viewerProject.AuthInstance.Resources) != 1 || viewerProject.AuthInstance.Resources[0].Values["name"] != "Workforce updated by admin" {
		t.Fatalf("viewer mutation changed shared auth resource state: %+v", viewerProject.AuthInstance)
	}

	otherProjectPrincipal := ownerPrincipal
	otherProjectPrincipal.ProjectID = otherProjectID
	otherProjectMux := authTestMux(t, store, otherProjectPrincipal)
	otherProjectPage := httptest.NewRecorder()
	otherProjectMux.ServeHTTP(otherProjectPage, httptest.NewRequest(http.MethodGet, "/auth/connections", nil))
	if otherProjectPage.Code != http.StatusOK || strings.Contains(otherProjectPage.Body.String(), "Workforce") {
		t.Fatalf("authentication state leaked across projects: status=%d body=%s", otherProjectPage.Code, otherProjectPage.Body.String())
	}
	otherProjectAuditPage := httptest.NewRecorder()
	otherProjectMux.ServeHTTP(otherProjectAuditPage, httptest.NewRequest(http.MethodGet, "/auth/audit", nil))
	for _, unexpected := range []string{"auth.connections.created", "auth.connections.updated"} {
		if otherProjectAuditPage.Code != http.StatusOK || strings.Contains(otherProjectAuditPage.Body.String(), unexpected) {
			t.Fatalf("authentication audit leaked across projects via %q: status=%d body=%s", unexpected, otherProjectAuditPage.Code, otherProjectAuditPage.Body.String())
		}
	}
	if !strings.Contains(otherProjectAuditPage.Body.String(), "No authentication console changes are recorded for this project.") {
		t.Fatal("other project audit page did not report an empty project-scoped log")
	}
	ownerSnapshot, found := store.Snapshot(owner.ID)
	if !found || len(ownerSnapshot.Organizations[ownerPrincipal.OrganizationID].Projects[otherProjectID].AuthInstance.Resources) != 0 {
		t.Fatal("authentication resources were persisted to another project")
	}
}

func TestAuthenticationValidationRejectsInvalidIssuerAndClaims(t *testing.T) {
	connection, _ := findSection("connections")
	if message := validate(connection, map[string]string{"name": "SSO", "protocol": "OIDC", "issuer": "http://identity.example.test"}); message == "" {
		t.Fatal("connection accepted a non-HTTPS issuer")
	}
	template, _ := findSection("templates")
	if message := validate(template, map[string]string{"name": "api", "claims": "[]", "lifetime_seconds": "300"}); message == "" {
		t.Fatal("template accepted a non-object claims payload")
	}
}

func TestAuthenticationTemplateRendersDemoBoundary(t *testing.T) {
	store := core.NewStore()
	account, err := store.CreateAccount(core.User{ID: "auth-page-user", Name: "Page user", Email: "page@example.test", PasswordHash: "unused"})
	if err != nil {
		t.Fatal(err)
	}
	layouts, err := fs.Sub(ui.Assets, "layouts")
	if err != nil {
		t.Fatal(err)
	}
	csrf := authTestCSRF{}
	renderer, err := render.New(layouts, store, csrf)
	if err != nil {
		t.Fatal(err)
	}
	mux := http.NewServeMux()
	Mount(mux, core.Deps{Store: store, Sessions: authTestSessions{account.Principal()}, CSRF: csrf, Render: renderer, Demo: true})
	response := httptest.NewRecorder()
	mux.ServeHTTP(response, httptest.NewRequest(http.MethodGet, "/auth/connections", nil))
	if response.Code != http.StatusOK {
		t.Fatalf("authentication page returned %d: %s", response.Code, response.Body.String())
	}
	for _, expected := range []string{"Demo simulation.", "not sent to ts-auth", "Add Connections", "client_secret"} {
		if !strings.Contains(response.Body.String(), expected) {
			t.Errorf("authentication page missing %q", expected)
		}
	}
}

func sharedAuthAccounts(t *testing.T) (*core.Store, core.Account, core.Account, core.Account, string) {
	t.Helper()
	store := core.NewStore()
	owner, err := store.CreateAccount(core.User{ID: "owner-1", Name: "Owner", Email: "owner@example.test", PasswordHash: "unused"})
	if err != nil {
		t.Fatal(err)
	}
	admin, err := store.CreateAccount(core.User{ID: "admin-1", Name: "Admin", Email: "admin@example.test", PasswordHash: "unused"})
	if err != nil {
		t.Fatal(err)
	}
	viewer, err := store.CreateAccount(core.User{ID: "viewer-1", Name: "Viewer", Email: "viewer@example.test", PasswordHash: "unused"})
	if err != nil {
		t.Fatal(err)
	}
	ownerPrincipal := owner.Principal()
	otherProjectID := "project-other"
	if err := store.Update(owner.ID, func(current *core.Account) error {
		org := current.Organizations[ownerPrincipal.OrganizationID]
		org.Members[admin.User.ID] = core.Member{UserID: admin.User.ID, Email: admin.User.Email, Name: admin.User.Name, Role: "admin", Status: "active"}
		org.Members[viewer.User.ID] = core.Member{UserID: viewer.User.ID, Email: viewer.User.Email, Name: viewer.User.Name, Role: "viewer", Status: "active"}
		org.Projects[otherProjectID] = core.Project{
			ID: otherProjectID, OrganizationID: org.ID, Name: "Isolated project",
			Databases: make(map[string]core.Database), APIKeys: make(map[string]core.APIKey),
			SavedQueries: make(map[string]core.SavedQuery), BackupPolicies: make(map[string]core.BackupPolicy),
		}
		current.Organizations[org.ID] = org
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	ownerSnapshot, found := store.Snapshot(owner.ID)
	if !found {
		t.Fatal("owner account disappeared while creating shared project")
	}
	sharedOrg := ownerSnapshot.Organizations[ownerPrincipal.OrganizationID]
	for _, member := range []core.Account{admin, viewer} {
		if err := store.Update(member.ID, func(current *core.Account) error {
			current.Organizations[sharedOrg.ID] = sharedOrg
			return nil
		}); err != nil {
			t.Fatalf("attach shared organization to %s: %v", member.User.Email, err)
		}
	}
	return store, owner, admin, viewer, otherProjectID
}

func sharedPrincipal(account core.Account, organizationID, projectID, role string) core.Principal {
	return core.Principal{
		UserID: account.User.ID, AccountID: account.ID, OrganizationID: organizationID,
		ProjectID: projectID, Role: role, AuthVersion: account.AuthVersion,
	}
}

func authTestMux(t *testing.T, store *core.Store, principal core.Principal) *http.ServeMux {
	t.Helper()
	layouts, err := fs.Sub(ui.Assets, "layouts")
	if err != nil {
		t.Fatal(err)
	}
	csrf := authTestCSRF{}
	renderer, err := render.New(layouts, store, csrf)
	if err != nil {
		t.Fatal(err)
	}
	mux := http.NewServeMux()
	Mount(mux, core.Deps{Store: store, Sessions: authTestSessions{principal}, CSRF: csrf, Render: renderer, Demo: true})
	return mux
}

type authTestSessions struct{ principal core.Principal }

func (s authTestSessions) Start(http.ResponseWriter, *http.Request, core.Principal) error {
	return errors.New("unused")
}
func (s authTestSessions) Principal(*http.Request) (core.Principal, bool) { return s.principal, true }
func (s authTestSessions) BearerPrincipal(*http.Request) (core.Principal, bool) {
	return s.principal, true
}
func (authTestSessions) End(http.ResponseWriter, *http.Request) {}
func (authTestSessions) RevokeUser(string, string)              {}

type authTestCSRF struct{}

func (authTestCSRF) Token(http.ResponseWriter, *http.Request) string { return "csrf" }
func (authTestCSRF) Verify(*http.Request) error                      { return nil }
func (authTestCSRF) Protect(next http.Handler) http.Handler          { return next }
