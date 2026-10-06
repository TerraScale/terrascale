package e2e

import (
	"net/url"
	"testing"

	"github.com/mxschmitt/playwright-go"
)

// TestNavigationContextSwitchStaysInSection switches project from a database
// page and organization from Team, and opens the project pages of an
// organization that has no project yet.
func TestNavigationContextSwitchStaysInSection(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	registerAndOnboard(t, page, newAccount("context-switch"))

	goToOK(t, page, "/organization?newProject=1")
	dialog := page.Locator("dialog#create-project")
	visible(t, dialog)
	fill(t, dialog.Locator(`[name="name"]`), "Second")
	submitAndExpect(t, page, "/api/v1/dashboard/organization/projects", 303, byRole(dialog, *playwright.AriaRoleButton, "Create project", true).Click)

	goToOK(t, page, "/databases")
	createDatabaseFromUI(t, page, "orders")
	switchContext(t, page, "data-project-option", "Second")
	waitForPath(t, page, func(u *url.URL) bool { return u.Path == "/databases" && u.RawQuery == "" })
	must(t, expect.Locator(page.Locator("[data-active-project-id]")).ToHaveText("Current project: Second"))

	goToOK(t, page, "/organization?newOrganization=organization")
	wizard := page.Locator("dialog#create-organization")
	organization := wizardStep(page, wizard, "organization")
	fill(t, organization.Locator(`[name="name"]`), uniqueName("Context business"))
	submitAndExpect(t, page, newOrganizationPath, 303, byRole(organization, *playwright.AriaRoleButton, "Create organization", true).Click)
	visible(t, byRole(wizard, *playwright.AriaRoleHeading, "Set up billing"))

	// The new organization has no project: every project page leads back to
	// its setup instead of a plain-text error.
	for _, path := range []string{"/", "/databases", "/auth", "/auth/users", "/usage"} {
		goToOK(t, page, path)
		waitForPath(t, page, func(u *url.URL) bool {
			return u.Path == "/organization" && u.Query().Get("newOrganization") == "project"
		})
	}

	goToOK(t, page, "/organization?tab=members")
	switchContext(t, page, "data-organization-option", "Personal")
	waitForPath(t, page, func(u *url.URL) bool { return u.Path == "/organization" })
	must(t, expect.Locator(page.Locator("[data-active-organization-id]")).ToHaveText("Current organization: Personal"))
}

// switchContext picks the named organization or project in the sidebar
// switcher; attr is data-organization-option or data-project-option.
func switchContext(t *testing.T, page playwright.Page, attr, name string) {
	t.Helper()
	click(t, page.Locator("details.ts-context-menu > summary"))
	click(t, page.Locator("["+attr+"]").Filter(playwright.LocatorFilterOptions{HasText: name}))
}
