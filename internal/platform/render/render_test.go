package render

import (
	"maps"
	"net/http/httptest"
	"slices"
	"strings"
	"testing"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/ui"
)

func TestShellReturnURLIsTheCurrentSection(t *testing.T) {
	t.Parallel()
	for target, want := range map[string]string{
		"/":                                    "/",
		"/?tab=connect&range=24h":              "/",
		"/databases":                           "/databases",
		"/databases/db_1/data?collection=a":    "/databases",
		"/auth":                                "/auth/overview",
		"/auth/overview":                       "/auth/overview",
		"/auth/users?q=ada&page=2":             "/auth/users",
		"/usage?project=prj_1&tab=billing":     "/usage",
		"/organization?newOrganization=create": "/organization",
		"/profile?tab=security":                "/profile?tab=security",
		"//evil.example/path":                  "/",
	} {
		shell := shellFor(core.Account{}, core.Principal{}, "shell", httptest.NewRequest("GET", target, nil))
		if shell.ReturnURL != want {
			t.Errorf("ReturnURL on %s = %q, want %q", target, shell.ReturnURL, want)
		}
	}
}

func TestShellNavigationNamesItsCounts(t *testing.T) {
	t.Parallel()
	p := core.Principal{AccountID: "acc_1", UserID: "usr_1", OrganizationID: "org_1", ProjectID: "prj_1"}
	account := core.Account{
		ID: p.AccountID, User: core.User{ID: p.UserID},
		Organizations: map[string]core.Organization{
			"org_1": {Name: "Acme", OwnerUserID: p.UserID},
			"org_2": {Name: "Globex", OwnerUserID: p.UserID},
		},
	}
	for range 120 {
		account.Notifications = append(account.Notifications, core.Notification{OrganizationID: "org_1", Status: "unread"})
	}
	account.Notifications = append(account.Notifications,
		core.Notification{OrganizationID: "org_1", Status: "read"},
		core.Notification{OrganizationID: "org_2", Status: "unread"},
	)

	shell := shellFor(account, p, "shell", httptest.NewRequest("GET", "/", nil))
	want := map[string]struct {
		count int
		label string
	}{
		"/organization": {2, "2 organizations"},
	}
	for _, group := range shell.Navigation {
		for _, item := range group.Items {
			if item.URL == "/notifications" {
				t.Error("notifications left the sidebar navigation for the account menu")
			}
			if expected, ok := want[item.URL]; ok {
				if item.BadgeCount != expected.count || item.BadgeLabel != expected.label {
					t.Errorf("%s badge = %d %q, want %d %q", item.URL, item.BadgeCount, item.BadgeLabel, expected.count, expected.label)
				}
				delete(want, item.URL)
			}
		}
	}
	if len(want) > 0 {
		t.Errorf("navigation lacks %v", slices.Collect(maps.Keys(want)))
	}
	// The unread count moved into the shell with the account, feeding the
	// account-menu entry and the top-bar bell instead of a sidebar badge.
	if shell.UnreadNotifications != 120 {
		t.Errorf("UnreadNotifications = %d, want 120", shell.UnreadNotifications)
	}
}

func TestContentSecurityPolicyReportURI(t *testing.T) {
	features := &ui.PageFeatures{}
	if policy := contentSecurityPolicy("n", features, ""); strings.Contains(policy, "report-") {
		t.Fatalf("policy reports without a URL: %s", policy)
	}
	url := "https://glitchtip.example/api/1/security/?glitchtip_key=abc"
	if policy := contentSecurityPolicy("n", features, url); !strings.HasSuffix(policy, "; report-uri "+url) {
		t.Fatalf("policy=%s", policy)
	}
}
