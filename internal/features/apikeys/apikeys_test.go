package apikeys

import (
	"bytes"
	"context"
	json "encoding/json/v2"
	"net/http"
	"net/http/httptest"
	"regexp"
	"strings"
	"testing"
	"time"

	"terrascale.tech/dashboard/internal/core"
	sdk "terrascale.tech/sdk"
)

func TestAPIKeyContentEscapesOneTimeSecret(t *testing.T) {
	var out bytes.Buffer
	secret := `secret<script>alert(1)</script>`
	if err := apiKeysContent(core.Page{CSRFToken: "test-csrf"}, pageData{CreatedSecret: secret}).Render(context.Background(), &out); err != nil {
		t.Fatal(err)
	}
	if strings.Contains(out.String(), "<script>") || !strings.Contains(out.String(), "&lt;script&gt;") {
		t.Fatalf("secret was not HTML escaped: %s", out.String())
	}
}

type testSessions struct{ p core.Principal }

func (s testSessions) Start(http.ResponseWriter, *http.Request, core.Principal) error { return nil }
func (s testSessions) Principal(*http.Request) (core.Principal, bool)                 { return s.p, true }
func (s testSessions) BearerPrincipal(r *http.Request) (core.Principal, bool) {
	return s.p, r.Header.Get("Authorization") == "Bearer dashboard-session"
}
func (s testSessions) Token(*http.Request) (string, bool)     { return "dashboard-session", true }
func (s testSessions) End(http.ResponseWriter, *http.Request) {}
func (s testSessions) RevokeUser(string, string)              {}

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

func TestCredentialPageFiltersProjectKeys(t *testing.T) {
	deps, p := fixture(t, "owner")
	if err := deps.Store.UpdateAs(p, func(a *core.Account) error {
		org := a.Organizations[p.OrganizationID]
		project := org.Projects[p.ProjectID]
		project.APIKeys["m2m-1"] = core.APIKey{ID: "m2m-1", Kind: "m2m", ProjectID: p.ProjectID, DatabaseID: "db-2", Name: "worker", Scopes: []string{"database:read"}}
		project.APIKeys["old-1"] = core.APIKey{ID: "old-1", Kind: "api-key", ProjectID: p.ProjectID, Name: "revoked", RevokedAt: time.Now().UTC(), Scopes: []string{"database:read"}}
		org.Projects[p.ProjectID] = project
		a.Organizations[p.OrganizationID] = org
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	deps.Render = renderingRenderer{}
	mux := http.NewServeMux()
	Mount(mux, deps)
	for _, tc := range []struct{ path, want, absent string }{
		{"/api-keys?kind=m2m&status=active&databaseId=db-2&q=work", "worker", "Existing"},
		{"/api-keys?kind=key&status=inactive", "revoked", "worker"},
		{"/api-keys?kind=key&q=Existing", "Existing", "revoked"},
	} {
		t.Run(tc.path, func(t *testing.T) {
			w := httptest.NewRecorder()
			mux.ServeHTTP(w, httptest.NewRequest(http.MethodGet, tc.path, nil))
			if w.Code != http.StatusOK || !strings.Contains(w.Body.String(), ">"+tc.want+"<") || strings.Contains(w.Body.String(), ">"+tc.absent+"<") {
				t.Fatalf("credential filter status %d: %s", w.Code, w.Body.String())
			}
		})
	}
}

func TestCredentialCreationDisclosuresStartClosedAndOpenSelectedForm(t *testing.T) {
	deps, _ := fixture(t, "owner")
	deps.Render = renderingRenderer{}
	mux := http.NewServeMux()
	Mount(mux, deps)
	for _, tc := range []struct {
		path    string
		openKey bool
		openM2M bool
	}{
		{path: "/api-keys"},
		{path: "/api-keys?create=key", openKey: true},
		{path: "/api-keys?create=m2m", openM2M: true},
		{path: "/api-keys?create=unknown"},
	} {
		t.Run(tc.path, func(t *testing.T) {
			w := httptest.NewRecorder()
			mux.ServeHTTP(w, httptest.NewRequest(http.MethodGet, tc.path, nil))
			if w.Code != http.StatusOK || !strings.Contains(w.Body.String(), ">Existing<") {
				t.Fatalf("credential list status %d: %s", w.Code, w.Body.String())
			}
			openings := regexp.MustCompile(`<details[^>]*>`).FindAllString(w.Body.String(), -1)
			if len(openings) != 2 || strings.Contains(openings[0], " open") != tc.openKey || strings.Contains(openings[1], " open") != tc.openM2M {
				t.Fatalf("creation disclosure state = %v, want key=%t m2m=%t", openings, tc.openKey, tc.openM2M)
			}
			if !strings.Contains(w.Body.String(), `action="/api/v1/dashboard/credentials/key"`) || !strings.Contains(w.Body.String(), `action="/api/v1/dashboard/credentials/m2m"`) || !strings.Contains(w.Body.String(), `name="_csrf" value="test-token"`) {
				t.Fatal("creation forms lost their endpoint or CSRF token")
			}
		})
	}
	viewer, _ := fixture(t, "viewer")
	viewer.Render = renderingRenderer{}
	viewerMux := http.NewServeMux()
	Mount(viewerMux, viewer)
	w := httptest.NewRecorder()
	viewerMux.ServeHTTP(w, httptest.NewRequest(http.MethodGet, "/api-keys?create=key", nil))
	if w.Code != http.StatusOK || strings.Contains(w.Body.String(), `action="/api/v1/dashboard/credentials/key"`) {
		t.Fatalf("viewer saw creation form: status %d: %s", w.Code, w.Body.String())
	}
}

func TestCredentialSummaryCountsScopedInventoryBeforeFilters(t *testing.T) {
	deps, p := fixture(t, "owner")
	if err := deps.Store.UpdateAs(p, func(a *core.Account) error {
		org := a.Organizations[p.OrganizationID]
		project := org.Projects[p.ProjectID]
		project.APIKeys["soon"] = core.APIKey{ID: "soon", Kind: "api-key", ProjectID: p.ProjectID, Name: "soon", ExpiresAt: time.Now().Add(7 * 24 * time.Hour)}
		project.APIKeys["expired"] = core.APIKey{ID: "expired", Kind: "api-key", ProjectID: p.ProjectID, Name: "expired", ExpiresAt: time.Now().Add(-time.Hour)}
		project.APIKeys["revoked"] = core.APIKey{ID: "revoked", Kind: "api-key", ProjectID: p.ProjectID, Name: "revoked", RevokedAt: time.Now()}
		project.APIKeys["machine"] = core.APIKey{ID: "machine", Kind: "m2m", ProjectID: p.ProjectID, Name: "machine"}
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
	mux.ServeHTTP(w, httptest.NewRequest(http.MethodGet, "/api-keys?kind=m2m", nil))
	if w.Code != http.StatusOK {
		t.Fatalf("credential summary status = %d: %s", w.Code, w.Body.String())
	}
	for label, want := range map[string]string{"Active keys": "2", "Expiring keys": "1", "M2M clients": "1"} {
		pattern := regexp.MustCompile(`(?s)<dt>` + regexp.QuoteMeta(label) + `</dt>\s*<dd><span class="ts-kpi-value">(\d+)</span>`)
		match := pattern.FindStringSubmatch(w.Body.String())
		if len(match) != 2 || match[1] != want {
			t.Fatalf("%s count = %v, want %s: %s", label, match, want, w.Body.String())
		}
	}
	if strings.Contains(w.Body.String(), ">Existing<") || !strings.Contains(w.Body.String(), ">machine<") {
		t.Fatalf("credential filter did not apply to rows: %s", w.Body.String())
	}
}

type countingIssuer struct{ calls int }

func (i *countingIssuer) Create(context.Context, core.Principal, sdk.CreateProjectAPIKeyRequest) (*sdk.ProjectAPIKeySecret, error) {
	i.calls++
	return nil, nil
}
func (i *countingIssuer) Rotate(context.Context, core.Principal, string) (*sdk.ProjectAPIKeySecret, error) {
	i.calls++
	return nil, nil
}
func (i *countingIssuer) Revoke(context.Context, core.Principal, string) (*sdk.ProjectAPIKeyRevocation, error) {
	i.calls++
	return nil, nil
}

func fixture(t *testing.T, role string) (core.Deps, core.Principal) {
	t.Helper()
	p := core.Principal{AccountID: "acct-1", UserID: "user-1", OrganizationID: "org-1", ProjectID: "project-1", Role: role}
	store := core.NewStore()
	if err := store.InsertAccount(core.Account{ID: p.AccountID, User: core.User{ID: p.UserID, Email: "tester@example.test", Name: "Tester", Role: role, PasswordHash: "test-hash"}, ActiveOrganizationID: p.OrganizationID, ActiveProjectID: p.ProjectID, Organizations: map[string]core.Organization{
		p.OrganizationID: {ID: p.OrganizationID, Kind: "business", Status: "active", BillingStatus: "active", OwnerUserID: "another-user", Members: map[string]core.Member{p.UserID: {UserID: p.UserID, Role: role, Status: "active"}}, Projects: map[string]core.Project{
			p.ProjectID: {ID: p.ProjectID, OrganizationID: p.OrganizationID, Name: "Project", Databases: map[string]core.Database{
				"db-1": {ID: "db-1", ProjectID: p.ProjectID, Name: "db-one", Status: "ready"},
				"db-2": {ID: "db-2", ProjectID: p.ProjectID, Name: "db-two", Status: "ready"},
			}, APIKeys: map[string]core.APIKey{"key-1": {ID: "key-1", Kind: "api-key", ProjectID: p.ProjectID, Name: "Existing", Scopes: []string{"database:read"}, SecretHash: "never-send-this"}}},
		}},
	}}); err != nil {
		t.Fatal(err)
	}
	return core.Deps{Store: store, Sessions: testSessions{p}, CSRF: testCSRF{}, Render: testRenderer{}, Demo: true}, p
}

func request(mux *http.ServeMux, method, path, body string) *httptest.ResponseRecorder {
	r := httptest.NewRequest(method, path, strings.NewReader(body))
	r.Header.Set("Authorization", "Bearer dashboard-session")
	r.Header.Set("Content-Type", "application/json")
	w := httptest.NewRecorder()
	mux.ServeHTTP(w, r)
	return w
}

func TestViewerAndMemberCannotCallLiveIssuer(t *testing.T) {
	for _, role := range []string{"viewer", "member"} {
		t.Run(role, func(t *testing.T) {
			deps, _ := fixture(t, role)
			deps.Demo = false
			issuer := &countingIssuer{}
			mux := http.NewServeMux()
			MountWithIssuer(mux, deps, issuer)
			root := "/api/v1/projects/project-1/auth/api-keys"
			for _, test := range []struct{ method, path, body string }{
				{http.MethodPost, root, `{"name":"denied","scopes":["database:read"]}`},
				{http.MethodPost, root + "/key-1/rotate", ""},
				{http.MethodDelete, root + "/key-1", ""},
			} {
				if got := request(mux, test.method, test.path, test.body).Code; got != http.StatusForbidden {
					t.Fatalf("%s %s: status %d, want 403", test.method, test.path, got)
				}
			}
			if issuer.calls != 0 {
				t.Fatalf("unauthorized role invoked issuer %d times", issuer.calls)
			}
			list := request(mux, http.MethodGet, root, "")
			if list.Code != http.StatusOK || strings.Contains(list.Body.String(), "never-send-this") {
				t.Fatalf("redacted metadata list: status %d body %s", list.Code, list.Body.String())
			}
		})
	}
}

func TestDemoKeyScopeRotationAndRevocation(t *testing.T) {
	deps, _ := fixture(t, "owner")
	mux := http.NewServeMux()
	Mount(mux, deps)
	root := "/api/v1/projects/project-1/auth/api-keys"
	created := request(mux, http.MethodPost, root, `{"name":"reader","scopes":["database:read"],"databaseId":"db-1"}`)
	if created.Code != http.StatusCreated {
		t.Fatalf("create: %d %s", created.Code, created.Body.String())
	}
	var first sdk.ProjectAPIKeySecret
	if err := json.Unmarshal(created.Body.Bytes(), &first); err != nil {
		t.Fatal(err)
	}
	if !AuthorizeDemoKey(deps.Store, "acct-1", "project-1", "db-1", first.Secret, "database:read") || AuthorizeDemoKey(deps.Store, "acct-1", "project-1", "db-1", first.Secret, "database:write") || AuthorizeDemoKey(deps.Store, "acct-1", "project-1", "db-2", first.Secret, "database:read") {
		t.Fatal("demo key capability or database scope was not enforced")
	}
	rotated := request(mux, http.MethodPost, root+"/"+first.APIKey.ID+"/rotate", "")
	if rotated.Code != http.StatusOK {
		t.Fatalf("rotate: %d %s", rotated.Code, rotated.Body.String())
	}
	var second sdk.ProjectAPIKeySecret
	if err := json.Unmarshal(rotated.Body.Bytes(), &second); err != nil {
		t.Fatal(err)
	}
	if AuthorizeDemoKey(deps.Store, "acct-1", "project-1", "db-1", first.Secret, "database:read") || !AuthorizeDemoKey(deps.Store, "acct-1", "project-1", "db-1", second.Secret, "database:read") {
		t.Fatal("rotation did not invalidate old secret")
	}
	if revoked := request(mux, http.MethodDelete, root+"/"+first.APIKey.ID, ""); revoked.Code != http.StatusOK {
		t.Fatalf("revoke: %d %s", revoked.Code, revoked.Body.String())
	}
	if AuthorizeDemoKey(deps.Store, "acct-1", "project-1", "db-1", second.Secret, "database:read") {
		t.Fatal("revoked key was still authorized")
	}
}
