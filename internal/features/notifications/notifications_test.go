package notifications

import (
	"html/template"
	"io/fs"
	"net/http"
	"net/http/httptest"
	"testing"
	"testing/fstest"
	"time"

	"terrascale.tech/dashboard/internal/core"
)

func TestMarkReadChangesOnlyTheAuthenticatedAccount(t *testing.T) {
	store := core.NewStore()
	first := testAccount("acct-one", "usr-one", "org-one", "prj-one", core.Notification{
		ID: "notification-one", OrganizationID: "org-one", ProjectID: "prj-one", Status: "Unread",
	})
	second := testAccount("acct-two", "usr-two", "org-two", "prj-two", core.Notification{
		ID: "notification-one", OrganizationID: "org-two", ProjectID: "prj-two", Status: "Unread",
	})
	if err := store.InsertAccount(first); err != nil {
		t.Fatal(err)
	}
	if err := store.InsertAccount(second); err != nil {
		t.Fatal(err)
	}
	p := first.Principal()
	deps := core.Deps{Store: store, Sessions: fixedSessions{principal: p}, CSRF: acceptCSRF{}, Render: testRenderer{}}
	mux := http.NewServeMux()
	Mount(mux, deps)

	request := httptest.NewRequest(http.MethodPost, "/api/v1/notifications/notification-one/read", nil)
	response := httptest.NewRecorder()
	mux.ServeHTTP(response, request)
	if response.Code != http.StatusSeeOther {
		t.Fatalf("status = %d, want %d; body=%s", response.Code, http.StatusSeeOther, response.Body.String())
	}

	updatedFirst, err := store.SnapshotAs(p)
	if err != nil {
		t.Fatal(err)
	}
	updatedSecond, ok := store.Snapshot(second.ID)
	if !ok {
		t.Fatal("second account disappeared")
	}
	if got := updatedFirst.Notifications[0].Status; got != "Read" {
		t.Fatalf("first notification status = %q, want Read", got)
	}
	if got := updatedSecond.Notifications[0].Status; got != "Unread" {
		t.Fatalf("second notification status = %q, want Unread", got)
	}
}

func TestMarkReadRejectsNotificationOutsideSelectedScope(t *testing.T) {
	store := core.NewStore()
	a := testAccount("acct-one", "usr-one", "org-one", "prj-one", core.Notification{
		ID: "other-project", OrganizationID: "org-one", ProjectID: "prj-other", Status: "Unread",
	})
	if err := store.InsertAccount(a); err != nil {
		t.Fatal(err)
	}
	deps := core.Deps{Store: store, Sessions: fixedSessions{principal: a.Principal()}, CSRF: acceptCSRF{}, Render: testRenderer{}}
	mux := http.NewServeMux()
	Mount(mux, deps)
	response := httptest.NewRecorder()
	mux.ServeHTTP(response, httptest.NewRequest(http.MethodPost, "/api/v1/notifications/other-project/read", nil))
	if response.Code != http.StatusNotFound {
		t.Fatalf("status = %d, want 404", response.Code)
	}
	updated, err := store.SnapshotAs(a.Principal())
	if err != nil {
		t.Fatal(err)
	}
	if updated.Notifications[0].Status != "Unread" {
		t.Fatal("out-of-scope notification was mutated")
	}
}

func TestNotificationTemplateParses(t *testing.T) {
	data, err := fs.ReadFile(featureTemplates, "templates/notifications.html")
	if err != nil {
		t.Fatal(err)
	}
	files := fstest.MapFS{"notifications.html": {Data: data}}
	if _, err := template.ParseFS(files, "*.html"); err != nil {
		t.Fatalf("parse feature template: %v", err)
	}
}

type fixedSessions struct{ principal core.Principal }

func (s fixedSessions) Start(http.ResponseWriter, *http.Request, core.Principal) error { return nil }
func (s fixedSessions) Principal(*http.Request) (core.Principal, bool)                 { return s.principal, true }
func (s fixedSessions) BearerPrincipal(*http.Request) (core.Principal, bool) {
	return core.Principal{}, false
}
func (s fixedSessions) Token(*http.Request) (string, bool)     { return "", false }
func (s fixedSessions) End(http.ResponseWriter, *http.Request) {}
func (s fixedSessions) RevokeUser(string, string)              {}

type acceptCSRF struct{}

func (acceptCSRF) Token(http.ResponseWriter, *http.Request) string { return "test-token" }
func (acceptCSRF) Verify(*http.Request) error                      { return nil }
func (acceptCSRF) Protect(next http.Handler) http.Handler          { return next }

type testRenderer struct{}

func (testRenderer) Register(string, fs.FS, ...string) error                          { return nil }
func (testRenderer) Page(http.ResponseWriter, *http.Request, core.Page) error         { return nil }
func (testRenderer) Fragment(http.ResponseWriter, *http.Request, core.Fragment) error { return nil }

func testAccount(accountID, userID, orgID, projectID string, notifications ...core.Notification) core.Account {
	user := core.User{ID: userID, Email: userID + "@example.test", Name: userID, PasswordHash: "test-password-hash", Role: "owner"}
	org := core.Organization{
		ID: orgID, OwnerUserID: userID,
		Members:  map[string]core.Member{userID: {UserID: userID, Role: "owner", Status: "active"}},
		Projects: map[string]core.Project{projectID: {ID: projectID, OrganizationID: orgID}},
	}
	return core.Account{
		ID: accountID, User: user, Organizations: map[string]core.Organization{orgID: org},
		ActiveOrganizationID: orgID, ActiveProjectID: projectID,
		Notifications: notifications, CreatedAt: time.Now().UTC(),
	}
}
