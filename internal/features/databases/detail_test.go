package databases

import (
	"net/http"
	"net/http/httptest"
	"net/url"
	"strings"
	"testing"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/features/backups"
)

func TestDatabaseDetailTabsAndScopedLookup(t *testing.T) {
	deps, principal := databaseFixture(t, "owner")
	deps.Render = renderingRenderer{}
	if err := deps.Store.UpdateAs(principal, func(account *core.Account) error {
		org := account.Organizations[principal.OrganizationID]
		project := org.Projects[principal.ProjectID]
		project.Databases["db-1"] = core.Database{ID: "db-1", ProjectID: project.ID, Name: "orders", Region: "GRU", Status: "ready"}
		org.Projects[project.ID] = project
		account.Organizations[org.ID] = org
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	mux := http.NewServeMux()
	Mount(mux, deps)
	for _, tc := range []struct {
		tab, content string
	}{
		{"overview", "Placement and latency"},
		{"placement", "Placement and latency"},
		{"backups", "Scheduled backups"},
		{"branches", "Create preview branch"},
		{"import", "Import JSONL / CSV"},
	} {
		t.Run(tc.tab, func(t *testing.T) {
			response := httptest.NewRecorder()
			mux.ServeHTTP(response, httptest.NewRequest(http.MethodGet, "/databases/db-1?tab="+tc.tab, nil))
			body := response.Body.String()
			if response.Code != http.StatusOK || !strings.Contains(body, tc.content) || strings.Count(body, `class="ts-tab" href="/databases/db-1?tab=`) != 5 || strings.Count(body, `aria-current="page"`) != 1 {
				t.Fatalf("tab %s: status %d, body %s", tc.tab, response.Code, body)
			}
			if !strings.Contains(body, "Not measured") && tc.tab == "placement" {
				t.Fatalf("non-fixture database claims measured latency: %s", body)
			}
		})
	}
	missing := httptest.NewRecorder()
	mux.ServeHTTP(missing, httptest.NewRequest(http.MethodGet, "/databases/db-other?tab=overview", nil))
	if missing.Code != http.StatusNotFound || strings.Contains(missing.Body.String(), "orders") {
		t.Fatalf("missing database detail: status %d, body %s", missing.Code, missing.Body.String())
	}
}

func TestDemoLatencyEstimateIsLimitedToSeededMarioDatabase(t *testing.T) {
	principal := core.Principal{UserID: "usr_01JC9Q7T4N8Y2M3P5R7V9X1ZAB", OrganizationID: "org_01JC9Q5V2N4P6T8R0X2Z4B6D8", ProjectID: "prj_01JC9Q6W3M5N7P9R2T4V6X8ZA"}
	account := core.Account{User: core.User{ID: principal.UserID, Email: "mario@terrascale.tech"}}
	project := core.Project{ID: "prj_01JC9Q6W3M5N7P9R2T4V6X8ZA"}
	database := core.Database{ID: "db_01JC9Q8Y5N7P9R3T5V7X9Z2AC", ProjectID: project.ID, Name: "core-primary", Region: "SFO"}
	for _, tc := range []struct {
		name    string
		demo    bool
		userID  string
		email   string
		orgID   string
		project string
		dbID    string
		sample  bool
	}{
		{"fixture", true, principal.UserID, account.User.Email, principal.OrganizationID, project.ID, database.ID, true},
		{"live", false, principal.UserID, account.User.Email, principal.OrganizationID, project.ID, database.ID, false},
		{"other user", true, "user-other", account.User.Email, principal.OrganizationID, project.ID, database.ID, false},
		{"other account", true, principal.UserID, "other@example.test", principal.OrganizationID, project.ID, database.ID, false},
		{"other organization", true, principal.UserID, account.User.Email, "org-other", project.ID, database.ID, false},
		{"other project", true, principal.UserID, account.User.Email, principal.OrganizationID, "project-other", database.ID, false},
		{"other database", true, principal.UserID, account.User.Email, principal.OrganizationID, project.ID, "db-other", false},
	} {
		t.Run(tc.name, func(t *testing.T) {
			principal.UserID = tc.userID
			account.User.Email = tc.email
			principal.OrganizationID = tc.orgID
			principal.ProjectID = tc.project
			project.ID = tc.project
			database.ProjectID = tc.project
			database.ID = tc.dbID
			info := makeDetailInfo(tc.demo, principal, account, project, database, "unknown", false)
			if len(info.Regions) != 1 || info.Regions[0].Sample != tc.sample || (strings.Contains(info.Regions[0].Estimate, "0 ms") != tc.sample) {
				t.Fatalf("sample scope %s: %#v", tc.name, info.Regions)
			}
			if info.Tab != "overview" {
				t.Fatalf("unknown tab normalized to %q", info.Tab)
			}
		})
	}
}

func TestDatabaseDetailBackupPolicyToggleReturnsToSelectedTab(t *testing.T) {
	deps, principal := databaseFixture(t, "owner")
	if err := deps.Store.UpdateAs(principal, func(account *core.Account) error {
		org := account.Organizations[principal.OrganizationID]
		project := org.Projects[principal.ProjectID]
		project.Databases["db-1"] = core.Database{ID: "db-1", ProjectID: project.ID, Name: "orders", Region: "GRU", Status: "ready"}
		project.BackupPolicies = map[string]core.BackupPolicy{"db-1": {FullBackupCron: "0 2 * * *", RetentionDays: 7}}
		org.Projects[project.ID] = project
		account.Organizations[org.ID] = org
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	mux := http.NewServeMux()
	Mount(mux, deps)
	backups.Mount(mux, deps)
	form := url.Values{
		"databaseId": {"db-1"}, "fullBackupCron": {"0 2 * * *"}, "retentionDays": {"7"},
		"disabled": {"1"}, "returnTo": {"databaseDetail"},
	}
	request := httptest.NewRequest(http.MethodPost, "/api/v1/dashboard/backups/policy", strings.NewReader(form.Encode()))
	request.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	request.Header.Set("Accept", "text/html")
	response := httptest.NewRecorder()
	mux.ServeHTTP(response, request)
	if response.Code != http.StatusSeeOther || response.Header().Get("Location") != "/databases/db-1?tab=backups" {
		t.Fatalf("toggle redirect: %d %q", response.Code, response.Header().Get("Location"))
	}
	account, err := deps.Store.SnapshotAs(principal)
	if err != nil {
		t.Fatal(err)
	}
	policy := account.Organizations[principal.OrganizationID].Projects[principal.ProjectID].BackupPolicies["db-1"]
	if !policy.Disabled || policy.FullBackupCron != "0 2 * * *" || policy.RetentionDays != 7 {
		t.Fatalf("policy after toggle: %#v", policy)
	}
	form.Set("returnTo", "//elsewhere.test")
	request = httptest.NewRequest(http.MethodPost, "/api/v1/dashboard/backups/policy", strings.NewReader(form.Encode()))
	request.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	request.Header.Set("Accept", "text/html")
	response = httptest.NewRecorder()
	mux.ServeHTTP(response, request)
	if response.Code != http.StatusSeeOther || response.Header().Get("Location") != "/backups?databaseId=db-1" {
		t.Fatalf("unsafe return target: %d %q", response.Code, response.Header().Get("Location"))
	}
}
