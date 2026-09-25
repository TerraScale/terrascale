package authentication

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
	for _, expected := range []string{
		"Audit log", "Authentication changes made in this project.",
		"auth.connections.created", "owner@example.test", "auth.connections.updated", "admin@example.test",
	} {
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
	if !strings.Contains(otherProjectAuditPage.Body.String(), "No authentication changes are recorded for this project.") {
		t.Fatal("other project audit page did not report an empty project-scoped log")
	}
	ownerSnapshot, found := store.Snapshot(owner.ID)
	if !found || len(ownerSnapshot.Organizations[ownerPrincipal.OrganizationID].Projects[otherProjectID].AuthInstance.Resources) != 0 {
		t.Fatal("authentication resources were persisted to another project")
	}
}

func TestAuthenticationConnectionsOfferEveryProvider(t *testing.T) {
	store := core.NewStore()
	account, err := store.CreateAccount(core.User{ID: "auth-page-user", Name: "Page user", Email: "page@example.test", PasswordHash: "unused"})
	if err != nil {
		t.Fatal(err)
	}
	mux := authTestMux(t, store, account.Principal())
	response := httptest.NewRecorder()
	mux.ServeHTTP(response, httptest.NewRequest(http.MethodGet, "/auth/connections", nil))
	if response.Code != http.StatusOK {
		t.Fatalf("authentication page returned %d: %s", response.Code, response.Body.String())
	}
	for _, expected := range []string{"Connect Google", "Connect Microsoft", "Connect GitHub", "Connect OIDC", "Connect SAML", `name="client_secret"`, "No sso connections yet"} {
		if !strings.Contains(response.Body.String(), expected) {
			t.Errorf("connections page missing %q", expected)
		}
	}
}

func TestAuthenticationValidationFollowsTSAuthRules(t *testing.T) {
	connections, _ := findSection("connections")
	oidc, _ := formFields(connections, "OIDC")
	for name, values := range map[string]map[string]string{
		"non-HTTPS issuer":     {"name": "SSO", "issuer": "http://identity.example.test", "client_id": "id", "client_secret": "secret"},
		"scopes without email": {"name": "SSO", "issuer": "https://identity.example.test", "client_id": "id", "client_secret": "secret", "scopes": "openid profile"},
	} {
		if validate(connections, oidc, values) == "" {
			t.Errorf("OIDC connection accepted %s", name)
		}
	}
	templates, _ := findSection("templates")
	for name, values := range map[string]map[string]string{
		"array claims":        {"name": "api", "claims": "[]", "lifetime_seconds": "300"},
		"reserved claim":      {"name": "api", "claims": `{"exp": "1"}`, "lifetime_seconds": "300"},
		"non-string claim":    {"name": "api", "claims": `{"admin": true}`, "lifetime_seconds": "300"},
		"unknown placeholder": {"name": "api", "claims": `{"sub": "{{user.mail}}"}`, "lifetime_seconds": "300"},
		"invalid name":        {"name": "my template", "claims": `{"sub": "{{user.id}}"}`, "lifetime_seconds": "300"},
	} {
		if validate(templates, templates.Fields, values) == "" {
			t.Errorf("JWT template accepted %s", name)
		}
	}
	if message := validate(templates, templates.Fields, map[string]string{"name": "supabase", "claims": `{"sub": "{{ user.id }}", "role": "{{org.role}}"}`, "lifetime_seconds": "300"}); message != "" {
		t.Errorf("valid JWT template rejected: %s", message)
	}
	clients, _ := findSection("clients")
	for uri, want := range map[string]bool{
		"https://app.example.com/callback": true, "http://localhost:3000/callback": true, "http://127.0.0.1/cb": true,
		"http://app.example.com/callback": false, "https://app.example.com/cb#fragment": false,
	} {
		got := validate(clients, clients.Fields, map[string]string{"name": "Web", "type": "none", "redirect_uris": uri}) == ""
		if got != want {
			t.Errorf("redirect URI %q accepted=%v, want %v", uri, got, want)
		}
	}
	domains, _ := findSection("domains")
	if validate(domains, domains.Fields, map[string]string{"domain": "-acme.com", "organization": "org_1"}) == "" {
		t.Error("enrollment domain accepted an invalid label")
	}
}

func TestAuthenticationSectionsRenderForManagersAndViewers(t *testing.T) {
	store, owner, _, viewer, _ := sharedAuthAccounts(t)
	ownerPrincipal := owner.Principal()
	ownerMux := authTestMux(t, store, ownerPrincipal)
	viewerMux := authTestMux(t, store, sharedPrincipal(viewer, ownerPrincipal.OrganizationID, ownerPrincipal.ProjectID, "viewer"))
	for _, current := range sections {
		for name, mux := range map[string]*http.ServeMux{"owner": ownerMux, "viewer": viewerMux} {
			response := httptest.NewRecorder()
			mux.ServeHTTP(response, httptest.NewRequest(http.MethodGet, "/auth/"+current.Key, nil))
			if response.Code != http.StatusOK || !strings.Contains(response.Body.String(), heading(current)) {
				t.Errorf("%s /auth/%s returned %d without its heading", name, current.Key, response.Code)
			}
			if name == "viewer" && strings.Contains(response.Body.String(), `commandfor="auth-create"`) {
				t.Errorf("viewer /auth/%s offers a create form", current.Key)
			}
		}
	}
	response := httptest.NewRecorder()
	ownerMux.ServeHTTP(response, httptest.NewRequest(http.MethodGet, "/auth/settings", nil))
	if response.Code != http.StatusNotFound {
		t.Fatalf("unknown section returned %d, want 404", response.Code)
	}
}

func TestAuthenticationStrategiesPersistAndStayCoherent(t *testing.T) {
	store, owner, _, _, _ := sharedAuthAccounts(t)
	principal := owner.Principal()
	mux := authTestMux(t, store, principal)
	settings := func() core.AuthSettings {
		account, _ := store.Snapshot(owner.ID)
		return effectiveSettings(account.Organizations[principal.OrganizationID].Projects[principal.ProjectID].AuthInstance)
	}
	if !settings().Passkeys || settings().Username {
		t.Fatalf("new instance does not use ts-auth defaults: %+v", settings())
	}
	for _, change := range []struct{ setting, enabled string }{
		{"username", "true"}, {"password", "false"}, {"magic_link", "false"}, {"email_passcode", "false"},
	} {
		response := post(mux, "/api/v1/auth-management/settings", url.Values{"setting": {change.setting}, "enabled": {change.enabled}})
		if response.Code != http.StatusSeeOther || strings.Contains(response.Header().Get("Location"), "error") {
			t.Fatalf("switching %s returned %d %s", change.setting, response.Code, response.Header().Get("Location"))
		}
	}
	response := post(mux, "/api/v1/auth-management/settings", url.Values{"setting": {"passkeys"}, "enabled": {"false"}})
	if !strings.Contains(response.Header().Get("Location"), "error=") || !settings().Passkeys {
		t.Fatalf("the last first factor was disabled: %s %+v", response.Header().Get("Location"), settings())
	}
	post(mux, "/api/v1/auth-management/settings", url.Values{"setting": {"totp"}, "enabled": {"false"}})
	if current := settings(); current.TOTP || current.RecoveryCodes || !current.Username || current.Password {
		t.Fatalf("strategy changes did not persist coherently: %+v", current)
	}
	response = post(mux, "/api/v1/auth-management/settings", url.Values{"setting": {"recovery_codes"}, "enabled": {"true"}})
	if !strings.Contains(response.Header().Get("Location"), "error=") || settings().RecoveryCodes {
		t.Fatal("recovery codes were enabled without an authenticator app")
	}
	page := httptest.NewRecorder()
	mux.ServeHTTP(page, httptest.NewRequest(http.MethodGet, "/auth/sign-in", nil))
	if !strings.Contains(page.Body.String(), `aria-checked="true" aria-labelledby="setting-username"`) {
		t.Fatal("sign-in page does not show the saved username switch")
	}
}

func TestAuthenticationRecordsKeepReferencesAndUniqueness(t *testing.T) {
	store, owner, _, _, _ := sharedAuthAccounts(t)
	principal := owner.Principal()
	mux := authTestMux(t, store, principal)
	instance := func() core.AuthInstance {
		account, _ := store.Snapshot(owner.ID)
		return account.Organizations[principal.OrganizationID].Projects[principal.ProjectID].AuthInstance
	}
	find := func(section string) core.AuthResource {
		t.Helper()
		for _, resource := range instance().Resources {
			if resource.Section == section {
				return resource
			}
		}
		t.Fatalf("no %s record stored", section)
		return core.AuthResource{}
	}

	if response := post(mux, "/api/v1/auth-management/memberships", url.Values{"organization": {"org_missing"}, "user": {"user_missing"}, "role": {"member"}}); !strings.Contains(response.Header().Get("Location"), "error=") {
		t.Fatal("membership accepted references to missing records")
	}
	post(mux, "/api/v1/auth-management/users", url.Values{"name": {"Ada Lovelace"}, "email": {"ada@example.test"}})
	if response := post(mux, "/api/v1/auth-management/users", url.Values{"name": {"Ada again"}, "email": {"ADA@example.test"}}); !strings.Contains(response.Header().Get("Location"), "error=") {
		t.Fatal("a second user claimed the same email address")
	}
	post(mux, "/api/v1/auth-management/organizations", url.Values{"name": {"Acme"}, "slug": {"acme"}})
	user, org := find("users"), find("organizations")
	post(mux, "/api/v1/auth-management/memberships", url.Values{"organization": {org.ID}, "user": {user.ID}, "role": {"admin"}})
	post(mux, "/api/v1/auth-management/domains", url.Values{"organization": {org.ID}, "domain": {"Acme.COM"}})
	if domain := find("domains"); domain.Values["domain"] != "acme.com" {
		t.Fatalf("enrollment domain was not normalized: %+v", domain.Values)
	}

	post(mux, "/api/v1/auth-management/organizations", url.Values{"id": {org.ID}, "name": {"Acme Corp"}, "slug": {"changed"}})
	if updated := find("organizations"); updated.Values["name"] != "Acme Corp" || updated.Values["slug"] != "acme" {
		t.Fatalf("organization update did not keep its immutable slug: %+v", updated.Values)
	}
	page := httptest.NewRecorder()
	mux.ServeHTTP(page, httptest.NewRequest(http.MethodGet, "/auth/memberships", nil))
	if body := page.Body.String(); !strings.Contains(body, "Ada Lovelace") || !strings.Contains(body, "Acme Corp") {
		t.Fatal("membership list does not name its user and organization")
	}

	post(mux, "/api/v1/auth-management/organizations/"+org.ID+"/delete", nil)
	for _, resource := range instance().Resources {
		if resource.Section == "memberships" || resource.Section == "domains" {
			t.Fatalf("deleting an organization left %s %s behind", resource.Section, resource.ID)
		}
	}

	post(mux, "/api/v1/auth-management/keys", url.Values{"name": {"Backend"}, "scopes": {"users", "webhooks"}})
	key := find("keys")
	if key.Values["scopes"] != "users,webhooks" {
		t.Fatalf("key scopes were not stored from the checkbox group: %+v", key.Values)
	}
	post(mux, "/api/v1/auth-management/keys/"+key.ID+"/delete", nil)
	if revoked := find("keys"); revoked.Status != "Revoked" {
		t.Fatalf("revoking a key removed or kept it active: %+v", revoked)
	}
	post(mux, "/api/v1/auth-management/users/"+user.ID+"/actions/block", nil)
	if blocked := find("users"); blocked.Status != "Blocked" {
		t.Fatalf("user block action did not persist: %+v", blocked)
	}
}

func TestAuthenticationBrandingRendersInPreview(t *testing.T) {
	store, owner, _, _, _ := sharedAuthAccounts(t)
	mux := authTestMux(t, store, owner.Principal())
	if response := post(mux, "/api/v1/auth-management/instance", url.Values{"name": {"Acme Identity"}, "logo_url": {"http://insecure.example.test/logo.svg"}, "theme_color": {"#ff0066"}}); !strings.Contains(response.Header().Get("Location"), "error=") {
		t.Fatal("branding accepted a non-HTTPS logo")
	}
	post(mux, "/api/v1/auth-management/instance", url.Values{"name": {"Acme Identity"}, "theme_color": {"#FF0066"}})
	page := httptest.NewRecorder()
	mux.ServeHTTP(page, httptest.NewRequest(http.MethodGet, "/auth/branding", nil))
	for _, expected := range []string{"Sign in to Acme Identity", `fill="#ff0066"`, `value="#ff0066"`} {
		if !strings.Contains(page.Body.String(), expected) {
			t.Errorf("branding page missing %q", expected)
		}
	}
	overview := httptest.NewRecorder()
	mux.ServeHTTP(overview, httptest.NewRequest(http.MethodGet, "/auth", nil))
	if !strings.Contains(overview.Body.String(), "1 of 6 steps complete") {
		t.Error("overview checklist does not count the saved branding")
	}
}

func post(mux *http.ServeMux, target string, form url.Values) *httptest.ResponseRecorder {
	request := httptest.NewRequest(http.MethodPost, target, strings.NewReader(form.Encode()))
	request.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	response := httptest.NewRecorder()
	mux.ServeHTTP(response, request)
	return response
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
	csrf := authTestCSRF{}
	renderer := render.New(store, csrf)
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

func TestSectionsMatchSidebarNavigation(t *testing.T) {
	navigation := core.AuthNavigation()
	if len(navigation) != len(sections) {
		t.Fatalf("sidebar lists %d sections, console has %d", len(navigation), len(sections))
	}
	for i, item := range navigation {
		if item.Label != sections[i].Title || item.URL != "/auth/"+sections[i].Key || item.Group != sections[i].Group {
			t.Errorf("sidebar item %d = %q %q %q, want %q /auth/%s %q", i, item.Label, item.URL, item.Group, sections[i].Title, sections[i].Key, sections[i].Group)
		}
	}
}
