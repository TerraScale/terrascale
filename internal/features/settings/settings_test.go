package settings

import (
	"io/fs"
	"net/http"
	"net/http/httptest"
	"net/url"
	"strings"
	"testing"
	"time"

	"terrascale.tech/dashboard/internal/core"
)

func TestViewerCannotChangeProjectAuthentication(t *testing.T) {
	store, account := settingsStore("viewer")
	deps := core.Deps{Store: store, Sessions: settingsSessions{account.Principal()}, CSRF: settingsCSRF{}, Render: settingsRenderer{}}
	mux := http.NewServeMux()
	Mount(mux, deps)
	response := httptest.NewRecorder()
	request := httptest.NewRequest(http.MethodPost, "/api/v1/dashboard/settings/authentication", strings.NewReader("enabled=true"))
	request.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	mux.ServeHTTP(response, request)
	if response.Code != http.StatusForbidden {
		t.Fatalf("status = %d, want 403", response.Code)
	}
	updated, err := store.SnapshotAs(account.Principal())
	if err != nil {
		t.Fatal(err)
	}
	if updated.Organizations["org-one"].Projects["prj-one"].AuthenticationEnabled {
		t.Fatal("viewer changed project authentication")
	}
}

func TestOwnerCanUpdateScopedSettingsAndProducesAuditEvent(t *testing.T) {
	store, account := settingsStore("owner")
	deps := core.Deps{Store: store, Sessions: settingsSessions{account.Principal()}, CSRF: settingsCSRF{}, Render: settingsRenderer{}}
	mux := http.NewServeMux()
	Mount(mux, deps)

	for path, body := range map[string]string{
		"/api/v1/dashboard/settings/authentication":  "enabled=true",
		"/api/v1/dashboard/settings/audit-retention": "retentionDays=30",
		"/api/v1/dashboard/settings/backup-policy":   "databaseId=db-one&fullBackupCron=0+2+*+*+*&incrementalBackupCron=0+*/6+*+*+*&retentionDays=12",
	} {
		request := httptest.NewRequest(http.MethodPost, path, strings.NewReader(body))
		request.Header.Set("Content-Type", "application/x-www-form-urlencoded")
		response := httptest.NewRecorder()
		mux.ServeHTTP(response, request)
		if response.Code != http.StatusSeeOther {
			t.Fatalf("POST %s status = %d, want 303; body=%s", path, response.Code, response.Body.String())
		}
	}

	updated, err := store.SnapshotAs(account.Principal())
	if err != nil {
		t.Fatal(err)
	}
	org := updated.Organizations["org-one"]
	project := org.Projects["prj-one"]
	if !project.AuthenticationEnabled || project.SecuritySettingsUpdatedAt.IsZero() {
		t.Fatalf("authentication settings = %+v", project)
	}
	if org.AuditRetentionDays != 30 {
		t.Fatalf("audit retention = %d, want 30", org.AuditRetentionDays)
	}
	if policy := project.BackupPolicies["db-one"]; policy.RetentionDays != 12 || policy.FullBackupCron != "0 2 * * *" {
		t.Fatalf("backup policy = %+v", policy)
	}
	if len(updated.AuditEvents) != 3 {
		t.Fatalf("audit event count = %d, want 3", len(updated.AuditEvents))
	}
}

func TestBackupPolicyCannotTargetAnotherProject(t *testing.T) {
	store, account := settingsStore("owner")
	org := account.Organizations["org-one"]
	org.Projects["prj-other"] = core.Project{
		ID: "prj-other", OrganizationID: "org-one", Name: "Other",
		Databases: map[string]core.Database{"db-other": {ID: "db-other", ProjectID: "prj-other", Name: "other-db"}},
	}
	account.Organizations[org.ID] = org
	if err := store.Update(account.ID, func(current *core.Account) error {
		current.Organizations[org.ID] = org
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	deps := core.Deps{Store: store, Sessions: settingsSessions{account.Principal()}, CSRF: settingsCSRF{}, Render: settingsRenderer{}}
	mux := http.NewServeMux()
	Mount(mux, deps)
	request := httptest.NewRequest(http.MethodPost, "/api/v1/dashboard/settings/backup-policy", strings.NewReader(url.Values{
		"databaseId": {"db-other"}, "fullBackupCron": {"0 2 * * *"}, "incrementalBackupCron": {"0 */6 * * *"}, "retentionDays": {"7"},
	}.Encode()))
	request.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	response := httptest.NewRecorder()
	mux.ServeHTTP(response, request)
	if response.Code != http.StatusNotFound {
		t.Fatalf("status = %d, want 404", response.Code)
	}
	updated, err := store.SnapshotAs(account.Principal())
	if err != nil {
		t.Fatal(err)
	}
	if _, exists := updated.Organizations[org.ID].Projects["prj-other"].BackupPolicies["db-other"]; exists {
		t.Fatal("cross-project backup policy was persisted")
	}
}

func settingsStore(role string) (*core.Store, core.Account) {
	store := core.NewStore()
	user := core.User{ID: "usr-one", Email: "owner@example.test", Name: "Account user", PasswordHash: "test-password-hash", Role: "owner"}
	ownerID := user.ID
	if role == "viewer" {
		ownerID = "usr-owner"
	}
	org := core.Organization{
		ID: "org-one", OwnerUserID: ownerID, AuditRetentionDays: 90,
		Members: map[string]core.Member{"usr-one": {UserID: "usr-one", Email: user.Email, Role: role, Status: "active"}},
		Projects: map[string]core.Project{
			"prj-one": {
				ID: "prj-one", OrganizationID: "org-one", Name: "Main project", Status: "active",
				Databases: map[string]core.Database{"db-one": {ID: "db-one", ProjectID: "prj-one", Name: "main-db"}},
				APIKeys:   make(map[string]core.APIKey), BackupPolicies: make(map[string]core.BackupPolicy),
			},
		},
	}
	account := core.Account{
		ID: "acct-one", User: user, Organizations: map[string]core.Organization{org.ID: org},
		ActiveOrganizationID: org.ID, ActiveProjectID: "prj-one", CreatedAt: time.Now().UTC(),
	}
	if err := store.InsertAccount(account); err != nil {
		panic(err)
	}
	return store, account
}

type settingsSessions struct{ p core.Principal }

func (s settingsSessions) Start(http.ResponseWriter, *http.Request, core.Principal) error { return nil }
func (s settingsSessions) Principal(*http.Request) (core.Principal, bool)                 { return s.p, true }
func (s settingsSessions) BearerPrincipal(*http.Request) (core.Principal, bool) {
	return core.Principal{}, false
}
func (s settingsSessions) Token(*http.Request) (string, bool)     { return "", false }
func (s settingsSessions) End(http.ResponseWriter, *http.Request) {}
func (s settingsSessions) RevokeUser(string, string)              {}

type settingsCSRF struct{}

func (settingsCSRF) Token(http.ResponseWriter, *http.Request) string { return "csrf" }
func (settingsCSRF) Verify(*http.Request) error                      { return nil }
func (settingsCSRF) Protect(next http.Handler) http.Handler          { return next }

type settingsRenderer struct{}

func (settingsRenderer) Register(string, fs.FS, ...string) error                  { return nil }
func (settingsRenderer) Page(http.ResponseWriter, *http.Request, core.Page) error { return nil }
func (settingsRenderer) Fragment(http.ResponseWriter, *http.Request, core.Fragment) error {
	return nil
}
