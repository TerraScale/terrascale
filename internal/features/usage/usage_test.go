package usage

import (
	"errors"
	"io/fs"
	"net/http"
	"net/http/httptest"
	"net/url"
	"strings"
	"testing"

	"terrascale.tech/dashboard/internal/core"
)

func TestParseCentsAndCapFormsRejectAmbiguousOrOverflowingValues(t *testing.T) {
	for input, want := range map[string]int64{"0": 0, "12": 1200, "12.3": 1230, "12.34": 1234} {
		got, ok := parseCents(input)
		if !ok || got != want {
			t.Errorf("parseCents(%q) = (%d, %t), want (%d, true)", input, got, ok, want)
		}
	}
	for _, input := range []string{"-1", "+1", "1.234", "1e3", "92233720368547759", "1.00.0"} {
		if got, ok := parseCents(input); ok {
			t.Errorf("parseCents(%q) = (%d, true), want invalid", input, got)
		}
	}
	for _, values := range []url.Values{{"spendCap": {"10", "20"}}} {
		if _, _, ok := formCap(values, "spendCap"); ok {
			t.Errorf("formCap(%v) unexpectedly accepted ambiguous values", values)
		}
	}
	if cents, found, valid := formCap(url.Values{"spendCap": {""}}, "spendCap"); !found || !valid || cents != 0 {
		t.Fatalf("empty optional cap = (%d, %t, %t), want (0, true, true)", cents, found, valid)
	}
	if _, valid := capsFromForm(url.Values{"dbCap_db1": {"1", "2"}}, "dbCap_"); valid {
		t.Fatal("capsFromForm accepted a duplicate cap value")
	}
}

func TestSpendingControlsPersistForOwnerAndRejectViewerWithStaleAdminRole(t *testing.T) {
	store := core.NewStore()
	account, err := store.CreateAccount(core.User{ID: "user-1", Name: "Owner", Email: "owner@example.test", PasswordHash: "unused"})
	if err != nil {
		t.Fatal(err)
	}
	principal := account.Principal()
	project := account.Organizations[principal.OrganizationID].Projects[principal.ProjectID]
	database := core.Database{ID: "db-1", ProjectID: project.ID, Name: "primary", Status: "ready"}
	if err := store.Update(account.ID, func(current *core.Account) error {
		org := current.Organizations[principal.OrganizationID]
		project := org.Projects[principal.ProjectID]
		project.Databases[database.ID] = database
		org.Projects[project.ID] = project
		current.Organizations[org.ID] = org
		return nil
	}); err != nil {
		t.Fatal(err)
	}

	mux := http.NewServeMux()
	deps := core.Deps{Store: store, Sessions: usageTestSessions{principal}, CSRF: usageTestCSRF{}, Render: usageTestRenderer{}}
	Mount(mux, deps)
	form := url.Values{
		"spendCap": {"45.67"}, "paidExcess": {"true"},
		"projCap_" + project.ID: {"20"}, "dbCap_" + database.ID: {"7.5"},
	}
	request := httptest.NewRequest(http.MethodPost, "/api/v1/dashboard/usage/spending-controls", strings.NewReader(form.Encode()))
	request.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	response := httptest.NewRecorder()
	mux.ServeHTTP(response, request)
	if response.Code != http.StatusSeeOther {
		t.Fatalf("owner update returned %d: %s", response.Code, response.Body.String())
	}
	updated, err := store.SnapshotAs(principal)
	if err != nil {
		t.Fatal(err)
	}
	updatedOrg := updated.Organizations[principal.OrganizationID]
	updatedProject := updatedOrg.Projects[project.ID]
	if updatedOrg.Billing.SpendCapCents != 4567 || !updatedOrg.Billing.PaidExcessEnabled || updatedProject.SpendCapCents != 2000 || updatedProject.Databases[database.ID].SpendCapCents != 750 {
		t.Fatalf("spending controls not persisted: org=%+v project=%+v", updatedOrg.Billing, updatedProject)
	}

	viewer, err := store.CreateAccount(core.User{ID: "user-2", Name: "Viewer", Email: "viewer@example.test", PasswordHash: "unused"})
	if err != nil {
		t.Fatal(err)
	}
	if err := store.Update(account.ID, func(current *core.Account) error {
		org := current.Organizations[principal.OrganizationID]
		org.Members[viewer.User.ID] = core.Member{UserID: viewer.User.ID, Email: viewer.User.Email, Name: viewer.User.Name, Role: "viewer", Status: "active"}
		current.Organizations[org.ID] = org
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	ownerSnapshot, found := store.Snapshot(account.ID)
	if !found {
		t.Fatal("owner account disappeared")
	}
	sharedOrg := ownerSnapshot.Organizations[principal.OrganizationID]
	if err := store.Update(viewer.ID, func(current *core.Account) error {
		current.Organizations[sharedOrg.ID] = sharedOrg
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	viewerPrincipal := core.Principal{
		UserID: viewer.User.ID, AccountID: viewer.ID, OrganizationID: principal.OrganizationID,
		ProjectID: principal.ProjectID, Role: "admin",
	}
	viewerMux := http.NewServeMux()
	viewerDeps := core.Deps{Store: store, Sessions: usageTestSessions{viewerPrincipal}, CSRF: usageTestCSRF{}, Render: usageTestRenderer{}}
	Mount(viewerMux, viewerDeps)
	request = httptest.NewRequest(http.MethodPost, "/api/v1/dashboard/usage/spending-controls", strings.NewReader(form.Encode()))
	request.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	response = httptest.NewRecorder()
	viewerMux.ServeHTTP(response, request)
	if response.Code != http.StatusForbidden {
		t.Fatalf("viewer with stale admin principal role returned %d, want forbidden", response.Code)
	}
	latest, found := store.Snapshot(account.ID)
	if !found {
		t.Fatal("updated account disappeared")
	}
	if latest.Organizations[principal.OrganizationID].Billing.SpendCapCents != 4567 {
		t.Fatal("viewer update changed the organization spending cap")
	}
}

func TestAddUsageDoesNotOverflow(t *testing.T) {
	const maxInt64 = int64(^uint64(0) >> 1)
	total := maxInt64 - 1
	if addUsage(&total, 1) != true || total != maxInt64 {
		t.Fatalf("last representable addition failed: total=%d", total)
	}
	if addUsage(&total, 1) {
		t.Fatal("overflowing usage addition was accepted")
	}
	if addUsage(&total, -1) {
		t.Fatal("negative usage addition was accepted")
	}
}

type usageTestSessions struct{ principal core.Principal }

func (s usageTestSessions) Start(http.ResponseWriter, *http.Request, core.Principal) error {
	return errors.New("unused")
}
func (s usageTestSessions) Principal(*http.Request) (core.Principal, bool) { return s.principal, true }
func (s usageTestSessions) BearerPrincipal(*http.Request) (core.Principal, bool) {
	return s.principal, true
}
func (usageTestSessions) End(http.ResponseWriter, *http.Request) {}
func (usageTestSessions) RevokeUser(string, string)              {}

type usageTestCSRF struct{}

func (usageTestCSRF) Token(http.ResponseWriter, *http.Request) string { return "csrf" }
func (usageTestCSRF) Verify(*http.Request) error                      { return nil }
func (usageTestCSRF) Protect(next http.Handler) http.Handler          { return next }

type usageTestRenderer struct{}

func (usageTestRenderer) Register(string, fs.FS, ...string) error                  { return nil }
func (usageTestRenderer) Page(http.ResponseWriter, *http.Request, core.Page) error { return nil }
func (usageTestRenderer) Fragment(http.ResponseWriter, *http.Request, core.Fragment) error {
	return nil
}
