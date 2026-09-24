package data

import (
	"bytes"
	"context"
	"net/http"
	"net/http/httptest"
	"net/url"
	"regexp"
	"strings"
	"testing"

	"terrascale.tech/dashboard/internal/core"
)

func TestDataContentEscapesDocumentJSONAndEncodesNavigation(t *testing.T) {
	d := pageData{
		Mode: "table", CanWrite: true, ProjectID: "project-1", DatabaseID: "db-1", Collection: "users&admin=1",
		Databases: []databaseChoice{{ID: "db-1", Name: "Primary"}},
		Documents: []documentView{{ID: "id?x=1", Value: []byte(`{"html":"</code><script>alert(1)</script>"}`)}},
	}
	var out bytes.Buffer
	if err := dataContent(core.Page{CSRFToken: "test-csrf"}, d).Render(context.Background(), &out); err != nil {
		t.Fatal(err)
	}
	body := out.String()
	if strings.Contains(body, "<script>") || !strings.Contains(body, "&lt;script&gt;") {
		t.Fatalf("document JSON was not HTML escaped: %s", body)
	}
	if !strings.Contains(body, "collection=users%26admin%3D1") || !strings.Contains(body, "id=id%3Fx%3D1") || !strings.Contains(body, "edit=id%3Fx%3D1") {
		t.Fatalf("document navigation was not URL encoded: %s", body)
	}
	if !strings.Contains(body, `name="operation" value="delete"`) || !strings.Contains(body, `name="_csrf" value="test-csrf"`) {
		t.Fatalf("document delete form or CSRF token missing: %s", body)
	}
}

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

func TestDataPagesRenderDistinctViews(t *testing.T) {
	deps, _ := dataFixture(t, "member")
	deps.Render = renderingRenderer{}
	mux := http.NewServeMux()
	Mount(mux, deps)
	for _, tc := range []struct{ path, heading, detail string }{
		{"/data", "Saved queries", "Project queries"},
		{"/data?view=table", "Collection documents", "Enter a collection name"},
		{"/data?view=editor", "Document editor", "Apply document operation"},
		{"/data?view=sql", "SQL console", "Run query"},
		{"/schema?tab=diff", "Schema", "no diff can be calculated"},
		{"/indexes?tab=recommendations", "Indexes", "unavailable without provider analysis data"},
	} {
		t.Run(tc.path, func(t *testing.T) {
			w := httptest.NewRecorder()
			mux.ServeHTTP(w, httptest.NewRequest(http.MethodGet, tc.path, nil))
			if w.Code != http.StatusOK || strings.Count(w.Body.String(), "<h1") != 1 || !strings.Contains(w.Body.String(), tc.heading) || !strings.Contains(w.Body.String(), tc.detail) {
				t.Fatalf("status %d, view %q: %s", w.Code, tc.path, w.Body.String())
			}
		})
	}
}

func TestSavedQueryCreateRendersModalAndNoScriptForm(t *testing.T) {
	deps, _ := dataFixture(t, "member")
	deps.Render = renderingRenderer{}
	mux := http.NewServeMux()
	Mount(mux, deps)
	for _, path := range []string{"/data?create=1", "/data?view=queries&create=1"} {
		t.Run(path, func(t *testing.T) {
			w := httptest.NewRecorder()
			mux.ServeHTTP(w, httptest.NewRequest(http.MethodGet, path, nil))
			body := w.Body.String()
			if w.Code != http.StatusOK || strings.Count(body, "<h1") != 1 || !strings.Contains(body, `<dialog id="save-query-dialog" class="ts-dialog" data-autoopen aria-labelledby="save-query-dialog-title">`) {
				t.Fatalf("modal route status %d: %s", w.Code, body)
			}
			noscript := regexp.MustCompile(`(?s)<noscript>(.*?)</noscript>`).FindStringSubmatch(body)
			if len(noscript) != 2 || !strings.Contains(noscript[1], `id="noscript-query-name"`) || !strings.Contains(noscript[1], `id="noscript-saved-sql"`) || !strings.Contains(noscript[1], `href="/data?view=queries"`) {
				t.Fatalf("no-JS fallback missing labels, form or cancel link: %s", body)
			}
			if !strings.Contains(body, `id="dialog-query-name"`) || !strings.Contains(body, `id="dialog-saved-sql"`) || !strings.Contains(body, `data-close-dialog`) || strings.Count(body, `action="/api/v1/dashboard/data/saved-queries"`) != 2 || strings.Count(body, `name="_csrf" value="test-token"`) != 2 {
				t.Fatalf("modal and fallback form contract missing: %s", body)
			}
		})
	}
	w := httptest.NewRecorder()
	mux.ServeHTTP(w, httptest.NewRequest(http.MethodGet, "/data", nil))
	if strings.Contains(w.Body.String(), `id="save-query-dialog"`) {
		t.Fatal("create dialog appeared on initial data page")
	}
	viewer, _ := dataFixture(t, "viewer")
	viewer.Render = renderingRenderer{}
	viewerMux := http.NewServeMux()
	Mount(viewerMux, viewer)
	w = httptest.NewRecorder()
	viewerMux.ServeHTTP(w, httptest.NewRequest(http.MethodGet, "/data?create=1", nil))
	if strings.Contains(w.Body.String(), `id="save-query-dialog"`) {
		t.Fatal("viewer saw save-query dialog")
	}
}

func TestSavedQueryBrowserRedirectsAndAPIContract(t *testing.T) {
	deps, p := dataFixture(t, "member")
	deps.Render = renderingRenderer{}
	mux := http.NewServeMux()
	Mount(mux, deps)
	post := func(path string, form url.Values, accept string) *httptest.ResponseRecorder {
		r := httptest.NewRequest(http.MethodPost, path, strings.NewReader(form.Encode()))
		r.Header.Set("Content-Type", "application/x-www-form-urlencoded")
		r.Header.Set("Accept", accept)
		w := httptest.NewRecorder()
		mux.ServeHTTP(w, r)
		return w
	}
	createPath := "/api/v1/dashboard/data/saved-queries"
	form := url.Values{"name": {"Find users"}, "sql": {"SELECT * FROM users LIMIT 20"}}
	w := post(createPath, form, "text/html")
	if w.Code != http.StatusSeeOther || w.Header().Get("Location") != "/data?view=queries" {
		t.Fatalf("browser create status %d, location %q", w.Code, w.Header().Get("Location"))
	}
	a, err := deps.Store.SnapshotAs(p)
	if err != nil {
		t.Fatal(err)
	}
	var id string
	for id = range a.Organizations[p.OrganizationID].Projects[p.ProjectID].SavedQueries {
	}
	w = post(createPath+"/delete", url.Values{"id": {id}}, "text/html")
	if w.Code != http.StatusSeeOther || w.Header().Get("Location") != "/data?view=queries" {
		t.Fatalf("browser delete status %d, location %q", w.Code, w.Header().Get("Location"))
	}
	w = post(createPath, form, "application/json")
	if w.Code != http.StatusCreated || !strings.Contains(w.Body.String(), "Find users") {
		t.Fatalf("API create status %d: %s", w.Code, w.Body.String())
	}
	viewer, _ := dataFixture(t, "viewer")
	viewerMux := http.NewServeMux()
	Mount(viewerMux, viewer)
	r := httptest.NewRequest(http.MethodPost, createPath, strings.NewReader(form.Encode()))
	r.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	w = httptest.NewRecorder()
	viewerMux.ServeHTTP(w, r)
	if w.Code != http.StatusForbidden {
		t.Fatalf("viewer create status = %d, want 403", w.Code)
	}
}

func TestSQLBrowserShowsQueryResult(t *testing.T) {
	deps, _ := dataFixture(t, "member")
	deps.Render = renderingRenderer{}
	mux := http.NewServeMux()
	Mount(mux, deps)
	if got := postDocument(mux, "db-1", "own").Code; got != http.StatusOK {
		t.Fatalf("seed document status = %d", got)
	}
	malicious := url.Values{"databaseId": {"db-1"}, "collection": {"users"}, "operation": {"create"}, "id": {"html"}, "document": {`{"value":"<script>alert(1)</script>"}`}}
	r := httptest.NewRequest(http.MethodPost, "/api/v1/dashboard/data/documents", strings.NewReader(malicious.Encode()))
	r.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	seed := httptest.NewRecorder()
	mux.ServeHTTP(seed, r)
	if seed.Code != http.StatusOK {
		t.Fatalf("seed HTML document status = %d", seed.Code)
	}
	form := url.Values{"databaseId": {"db-1"}, "sql": {"SELECT * FROM users LIMIT 20"}}
	request := func(accept string) *httptest.ResponseRecorder {
		r := httptest.NewRequest(http.MethodPost, "/api/v1/dashboard/data/sql", strings.NewReader(form.Encode()))
		r.Header.Set("Content-Type", "application/x-www-form-urlencoded")
		r.Header.Set("Accept", accept)
		w := httptest.NewRecorder()
		mux.ServeHTTP(w, r)
		return w
	}
	w := request("text/html")
	if w.Code != http.StatusOK || !strings.Contains(w.Body.String(), "Query result") || !strings.Contains(w.Body.String(), "9007199254740993") || !strings.Contains(w.Body.String(), "SELECT * FROM users LIMIT 20") || strings.Contains(w.Body.String(), "<script>") || !strings.Contains(w.Body.String(), "&lt;script&gt;") {
		t.Fatalf("browser SQL status %d: %s", w.Code, w.Body.String())
	}
	w = request("application/json")
	if w.Code != http.StatusOK || !strings.Contains(w.Body.String(), `"rows"`) || !strings.Contains(w.Body.String(), "9007199254740993") {
		t.Fatalf("API SQL status %d: %s", w.Code, w.Body.String())
	}
}

func TestDataPagesWithoutDatabaseHideMutations(t *testing.T) {
	deps, p := dataFixture(t, "member")
	if err := deps.Store.UpdateAs(p, func(a *core.Account) error {
		org := a.Organizations[p.OrganizationID]
		project := org.Projects[p.ProjectID]
		clear(project.Databases)
		org.Projects[p.ProjectID] = project
		a.Organizations[p.OrganizationID] = org
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	deps.Render = renderingRenderer{}
	mux := http.NewServeMux()
	Mount(mux, deps)
	for _, tc := range []struct{ path, action string }{
		{"/data?view=table", "Add document"},
		{"/data?view=editor", "Apply document operation"},
		{"/data?view=sql", "Run query"},
		{"/schema", "Apply schema"},
		{"/indexes", "Create index"},
	} {
		t.Run(tc.path, func(t *testing.T) {
			w := httptest.NewRecorder()
			mux.ServeHTTP(w, httptest.NewRequest(http.MethodGet, tc.path, nil))
			if w.Code != http.StatusOK || strings.Contains(w.Body.String(), ">"+tc.action+"<") {
				t.Fatalf("status %d, unexpected action %q: %s", w.Code, tc.action, w.Body.String())
			}
		})
	}
}

func TestDocumentBrowserMutationReturnsToTable(t *testing.T) {
	deps, p := dataFixture(t, "member")
	mux := http.NewServeMux()
	Mount(mux, deps)
	if got := postDocument(mux, "db-1", "own").Code; got != http.StatusOK {
		t.Fatalf("seed document status = %d", got)
	}
	form := url.Values{"databaseId": {"db-1"}, "collection": {"users"}, "operation": {"delete"}, "id": {"own"}}
	r := httptest.NewRequest(http.MethodPost, "/api/v1/dashboard/data/documents", strings.NewReader(form.Encode()))
	r.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	r.Header.Set("Accept", "text/html")
	w := httptest.NewRecorder()
	mux.ServeHTTP(w, r)
	if w.Code != http.StatusSeeOther || w.Header().Get("Location") != "/data?view=table&databaseId=db-1&collection=users" {
		t.Fatalf("browser delete status %d, location %q", w.Code, w.Header().Get("Location"))
	}
	a, err := deps.Store.SnapshotAs(p)
	if err != nil {
		t.Fatal(err)
	}
	if _, ok := a.Organizations[p.OrganizationID].Projects[p.ProjectID].Databases["db-1"].Documents["users"]["own"]; ok {
		t.Fatal("document remained after browser delete")
	}
}
