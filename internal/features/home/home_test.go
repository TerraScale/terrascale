package home

import (
	"errors"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/platform/render"
)

func TestOverviewRendersScopedCountsAndAnHonestEmptyChart(t *testing.T) {
	store := core.NewStore()
	account, err := store.CreateAccount(core.User{
		ID: "home-owner", Name: "Home Owner", Email: "home@example.test", PasswordHash: "unused",
	})
	if err != nil {
		t.Fatal(err)
	}
	p := account.Principal()
	otherProjectID := "project-other"
	if err := store.Update(account.ID, func(current *core.Account) error {
		org := current.Organizations[p.OrganizationID]
		project := org.Projects[p.ProjectID]
		project.Databases["database-one"] = core.Database{ID: "database-one", ProjectID: project.ID, Name: "one"}
		project.Databases["database-two"] = core.Database{ID: "database-two", ProjectID: project.ID, Name: "two"}
		project.APIKeys["active-key"] = core.APIKey{ID: "active-key", ProjectID: project.ID, Name: "active"}
		project.APIKeys["revoked-key"] = core.APIKey{ID: "revoked-key", ProjectID: project.ID, Name: "revoked", RevokedAt: account.CreatedAt}
		org.Projects[project.ID] = project
		org.Projects[otherProjectID] = core.Project{
			ID: otherProjectID, OrganizationID: org.ID, Name: "Other project",
			Databases: map[string]core.Database{"other-database": {ID: "other-database", ProjectID: otherProjectID}},
			APIKeys:   map[string]core.APIKey{"other-key": {ID: "other-key", ProjectID: otherProjectID}},
		}
		org.Members["active-member"] = core.Member{UserID: "active-member", Status: "active"}
		org.Members["inactive-member"] = core.Member{UserID: "inactive-member", Status: "inactive"}
		current.Organizations[org.ID] = org
		current.Notifications = []core.Notification{
			{ID: "project-unread", OrganizationID: org.ID, ProjectID: project.ID, Status: "unread"},
			{ID: "organization-read", OrganizationID: org.ID, Status: "read"},
			{ID: "other-project-unread", OrganizationID: org.ID, ProjectID: otherProjectID, Status: "unread"},
		}
		current.AuditEvents = []core.AuditEvent{
			{ID: "project-event", OrganizationID: org.ID, ProjectID: project.ID},
			{ID: "organization-event", OrganizationID: org.ID},
			{ID: "other-project-event", OrganizationID: org.ID, ProjectID: otherProjectID},
		}
		return nil
	}); err != nil {
		t.Fatal(err)
	}

	csrf := homeTestCSRF{}
	mux := http.NewServeMux()
	Mount(mux, core.Deps{
		Store: store, Sessions: homeTestSessions{principal: p}, CSRF: csrf, Render: render.New(store, csrf),
	})
	response := httptest.NewRecorder()
	mux.ServeHTTP(response, httptest.NewRequest(http.MethodGet, "/?range=30d", nil))
	if response.Code != http.StatusOK {
		t.Fatalf("overview returned %d: %s", response.Code, response.Body.String())
	}
	body := response.Body.String()
	for _, expected := range []string{
		"class=\"ts-home-overview\"", "class=\"ts-home-primary\"", "ts-home-getting-started\"",
		"Databases</dt><dd><span class=\"ts-kpi-value\">2</span>",
		"Organization members</dt><dd><span class=\"ts-kpi-value\">2</span>",
		"Unread alerts</dt><dd><span class=\"ts-kpi-value\">1</span>",
		"2 notifications loaded", "Events <strong>2</strong>", "No time-series activity samples are available yet.",
		"href=\"/?range=30d\"", "aria-current=\"page\"",
	} {
		if !strings.Contains(body, expected) {
			t.Errorf("overview is missing %q", expected)
		}
	}
	rangePicker := strings.Index(body, `class="ts-home-range-picker"`)
	if rangePicker < 0 || !strings.Contains(body[rangePicker:], `<ts-menu><details class="ts-menu ts-menu-end">`) {
		t.Fatal("activity range is not wrapped in the Rocket-enhanced menu")
	}
	if strings.Contains(body, "other-database") || strings.Contains(body, "other-project-event") || strings.Contains(body, "other-project-unread") {
		t.Fatal("overview exposed records scoped to another project")
	}
	if !strings.Contains(body, "Create first database") || !strings.Contains(body, "Create an Application credential") {
		t.Fatal("overview is missing the getting-started steps")
	}
	if strings.Count(body, "ts-badge-success\">Done") < 2 {
		t.Fatal("completed database and credential steps were not reflected in the overview")
	}
}

func TestConnectQueryRendersEscapedMCPConfiguration(t *testing.T) {
	store := core.NewStore()
	account, err := store.CreateAccount(core.User{
		ID: "home-connect-owner", Name: "Connect Owner", Email: "connect@example.test", PasswordHash: "unused",
	})
	if err != nil {
		t.Fatal(err)
	}
	csrf := homeTestCSRF{}
	mux := http.NewServeMux()
	Mount(mux, core.Deps{
		Store: store, Sessions: homeTestSessions{principal: account.Principal()}, CSRF: csrf, Render: render.New(store, csrf),
	})
	response := httptest.NewRecorder()
	mux.ServeHTTP(response, httptest.NewRequest(http.MethodGet, "/?tab=connect", nil))
	if response.Code != http.StatusOK {
		t.Fatalf("connect page returned %d: %s", response.Code, response.Body.String())
	}
	body := response.Body.String()
	for _, expected := range []string{
		"Connect your AI or platform", "AI assistants", "TypeScript SDK", "REST API", "CLI and CI",
		"https://&lt;your-terrascale-host&gt;/mcp", "Back to overview",
	} {
		if !strings.Contains(body, expected) {
			t.Errorf("connect page is missing %q", expected)
		}
	}
}

type homeTestSessions struct{ principal core.Principal }

func (s homeTestSessions) Start(http.ResponseWriter, *http.Request, core.Principal) error {
	return errors.New("unused")
}
func (s homeTestSessions) Principal(*http.Request) (core.Principal, bool) { return s.principal, true }
func (s homeTestSessions) BearerPrincipal(*http.Request) (core.Principal, bool) {
	return s.principal, true
}
func (homeTestSessions) End(http.ResponseWriter, *http.Request) {}
func (homeTestSessions) RevokeUser(string, string)              {}

type homeTestCSRF struct{}

func (homeTestCSRF) Token(http.ResponseWriter, *http.Request) string { return "csrf" }
func (homeTestCSRF) Verify(*http.Request) error                      { return nil }
func (homeTestCSRF) Protect(next http.Handler) http.Handler          { return next }
