package home

import (
	"errors"
	"fmt"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/features/demovisuals"
	"terrascale.tech/dashboard/internal/platform/render"
)

func TestOverviewRendersScopedCountsAndAnHonestZeroChart(t *testing.T) {
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
		"2 notifications loaded", "Plotted operations <strong>0</strong>", "Illustrative zero baseline",
		"ts-home-chart-shell", "ts-chart-line ts-chart-series-1", "30d ago",
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
	if strings.Contains(body, "Sample organization activity") {
		t.Fatal("fresh account received seeded Mario sample activity")
	}
	if !strings.Contains(body, "Create first database") || !strings.Contains(body, "Create an Application credential") {
		t.Fatal("overview is missing the getting-started steps")
	}
	if strings.Count(body, "ts-badge-success\">Done") < 2 {
		t.Fatal("completed database and credential steps were not reflected in the overview")
	}
}

func TestOverviewSeededMarioSampleChartChangesWithRange(t *testing.T) {
	store := core.NewStore()
	account, err := store.CreateAccount(core.User{ID: demovisuals.MarioUserID, Name: "Mario Demo", Email: "mario@example.test", PasswordHash: "unused"})
	if err != nil {
		t.Fatal(err)
	}
	if err := store.Update(account.ID, func(current *core.Account) error {
		original := current.Organizations[current.ActiveOrganizationID]
		project := original.Projects[current.ActiveProjectID]
		organization := original
		organization.ID = demovisuals.MarioOrganizationID
		organization.Slug = "terrascale-labs"
		organization.Name = "TerraScale Labs"
		organization.Projects = map[string]core.Project{project.ID: project}
		project.OrganizationID = organization.ID
		organization.Projects[project.ID] = project
		current.Organizations[organization.ID] = organization
		current.ActiveOrganizationID = organization.ID
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	account, _ = store.Snapshot(account.ID)
	csrf := homeTestCSRF{}
	mux := http.NewServeMux()
	Mount(mux, core.Deps{Store: store, Sessions: homeTestSessions{principal: account.Principal()}, CSRF: csrf, Render: render.New(store, csrf), Demo: true})
	for _, rangeName := range []string{"24h", "7d", "30d"} {
		response := httptest.NewRecorder()
		mux.ServeHTTP(response, httptest.NewRequest(http.MethodGet, "/?range="+rangeName, nil))
		if response.Code != http.StatusOK {
			t.Fatalf("%s overview returned %d", rangeName, response.Code)
		}
		body := response.Body.String()
		for _, expected := range []string{
			"Sample organization activity", "not measured provider traffic", "ts-chart-line ts-chart-series-1",
			fmt.Sprintf("Sample operations <strong>%d</strong>", demovisuals.Sum(demovisuals.Workload(rangeName))),
		} {
			if !strings.Contains(body, expected) {
				t.Errorf("%s overview missing %q", rangeName, expected)
			}
		}
	}
}

func TestOverviewResumesProjectlessBusinessOrganizationForOwner(t *testing.T) {
	for _, billingStatus := range []string{"billing_required", "active"} {
		t.Run(billingStatus, func(t *testing.T) {
			store, principal, _ := projectlessBusinessAccount(t)
			if err := store.Update(principal.AccountID, func(account *core.Account) error {
				org := account.Organizations[principal.OrganizationID]
				org.BillingStatus = billingStatus
				account.Organizations[org.ID] = org
				return nil
			}); err != nil {
				t.Fatal(err)
			}

			response := serveOverview(t, store, principal)
			if response.Code != http.StatusSeeOther {
				t.Fatalf("overview returned %d: %s", response.Code, response.Body.String())
			}
			if got := response.Header().Get("Location"); got != "/organization?newOrganization=project" {
				t.Fatalf("redirect location = %q", got)
			}
		})
	}
}

func TestOverviewDeniesOtherMissingProjectSelections(t *testing.T) {
	tests := []struct {
		name      string
		change    func(*core.Account, string)
		principal func(*core.Principal)
		want      int
	}{
		{
			name: "nonowner",
			change: func(account *core.Account, id string) {
				org := account.Organizations[id]
				org.OwnerUserID = "another-user"
				account.Organizations[id] = org
			},
		},
		{
			name: "stale account project selection",
			change: func(account *core.Account, _ string) {
				account.ActiveProjectID = "missing-project"
			},
		},
		{
			name: "inconsistent active organization",
			change: func(account *core.Account, _ string) {
				for id, org := range account.Organizations {
					if org.Kind == "personal" {
						account.ActiveOrganizationID = id
					}
				}
			},
		},
		{
			name: "personal organization",
			change: func(account *core.Account, id string) {
				org := account.Organizations[id]
				org.Kind = "personal"
				account.Organizations[id] = org
			},
		},
		{
			name: "archived organization",
			change: func(account *core.Account, id string) {
				org := account.Organizations[id]
				org.Archived = true
				account.Organizations[id] = org
			},
		},
		{
			name: "deletion requested",
			change: func(account *core.Account, id string) {
				org := account.Organizations[id]
				org.DeletionRequested = true
				account.Organizations[id] = org
			},
		},
		{
			name: "inactive organization",
			change: func(account *core.Account, id string) {
				org := account.Organizations[id]
				org.Status = "pending"
				account.Organizations[id] = org
			},
		},
		{
			name: "unsupported billing state",
			change: func(account *core.Account, id string) {
				org := account.Organizations[id]
				org.BillingStatus = "past_due"
				account.Organizations[id] = org
			},
		},
		{
			name: "existing project without selection",
			change: func(account *core.Account, id string) {
				org := account.Organizations[id]
				org.Projects["other-project"] = core.Project{ID: "other-project", OrganizationID: id}
				account.Organizations[id] = org
			},
		},
		{
			name: "explicit nonexistent project remains not found",
			principal: func(principal *core.Principal) {
				principal.ProjectID = "missing-project"
			},
			want: http.StatusNotFound,
		},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			store, principal, businessID := projectlessBusinessAccount(t)
			if test.change != nil {
				if err := store.Update(principal.AccountID, func(account *core.Account) error {
					test.change(account, businessID)
					return nil
				}); err != nil {
					t.Fatal(err)
				}
			}
			if test.principal != nil {
				test.principal(&principal)
			}
			response := serveOverview(t, store, principal)
			want := test.want
			if want == 0 {
				want = http.StatusForbidden
			}
			if response.Code != want {
				t.Fatalf("overview returned %d, want %d: %s", response.Code, want, response.Body.String())
			}
		})
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

func projectlessBusinessAccount(t *testing.T) (*core.Store, core.Principal, string) {
	t.Helper()
	store := core.NewStore()
	account, err := store.CreateAccount(core.User{
		ID: "home-business-owner", Name: "Business Owner", Email: "business@example.test", PasswordHash: "unused",
	})
	if err != nil {
		t.Fatal(err)
	}
	const businessID = "home-business"
	if err := store.Update(account.ID, func(current *core.Account) error {
		current.Organizations[businessID] = core.Organization{
			ID: businessID, Kind: "business", Status: "active", OwnerUserID: current.User.ID,
			BillingStatus: "billing_required", Projects: make(map[string]core.Project),
			Members: map[string]core.Member{
				current.User.ID: {UserID: current.User.ID, Role: "owner", Status: "active"},
			},
		}
		current.ActiveOrganizationID = businessID
		current.ActiveProjectID = ""
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	updated, ok := store.Snapshot(account.ID)
	if !ok {
		t.Fatal("business account is unavailable")
	}
	return store, updated.Principal(), businessID
}

func serveOverview(t *testing.T, store *core.Store, principal core.Principal) *httptest.ResponseRecorder {
	t.Helper()
	csrf := homeTestCSRF{}
	mux := http.NewServeMux()
	Mount(mux, core.Deps{
		Store: store, Sessions: homeTestSessions{principal: principal}, CSRF: csrf, Render: render.New(store, csrf),
	})
	response := httptest.NewRecorder()
	mux.ServeHTTP(response, httptest.NewRequest(http.MethodGet, "/", nil))
	return response
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
