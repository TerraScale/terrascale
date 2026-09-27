package e2e

import (
	"net/url"
	"strings"
	"testing"

	"github.com/mxschmitt/playwright-go"
)

// createDatabase submits the new-database dialog and expects the post to
// answer with status.
func createDatabase(t *testing.T, page playwright.Page, name, region string) {
	t.Helper()
	goToOK(t, page, "/databases?create=1")
	form := page.Locator(`form[action="/api/v1/dashboard/databases"]`)
	fill(t, form.Locator(`[name="name"]`), name)
	selectOption(t, form.Locator(`[name="region"]`), region)
	submitAndExpect(t, page, "/api/v1/dashboard/databases", 303, byRole(form, *playwright.AriaRoleButton, "Create database").Click)
}

func TestDatabaseFreePlanLimitIsExplainedAndHoldsForBranchesAndRestores(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	registerAndOnboard(t, page, newAccount("db-limit"))

	// The name field refuses what the server refuses.
	goToOK(t, page, "/databases?create=1")
	name := page.Locator("#database-name")
	fill(t, name, "trailing-")
	if valid, err := name.Evaluate(`input => input.checkValidity()`, nil); err != nil || valid != false {
		t.Fatalf("a trailing hyphen must fail the name pattern, valid = %v (%v)", valid, err)
	}

	first := uniqueName("first")
	createDatabase(t, page, first, "GRU")
	waitForPath(t, page, func(u *url.URL) bool { return strings.HasPrefix(u.Path, "/databases/db_") })
	databasePath := currentPath(t, page)

	// The Free plan's one database is in use: the dialog says so up front,
	// and a refused create keeps the name and region.
	second := uniqueName("second")
	createDatabase(t, page, second, "FRA")
	dialog := pageByRole(page, *playwright.AriaRoleDialog, "New database")
	visible(t, dialog)
	must(t, expect.Locator(dialog.GetByRole(*playwright.AriaRoleAlert)).ToContainText("The Free plan includes 1 database"))
	must(t, expect.Locator(byRole(dialog, *playwright.AriaRoleLink, "Compare plans")).ToHaveAttribute("href", "/billing"))
	must(t, expect.Locator(dialog.Locator("#database-name")).ToHaveValue(second))
	must(t, expect.Locator(dialog.Locator("#database-region")).ToHaveValue("FRA"))

	// Branches and restores count against the same limit.
	goToOK(t, page, databasePath+"/branches")
	submitAndExpect(t, page, "/api/v1/dashboard/databases/branch", 303, pageByRole(page, *playwright.AriaRoleButton, "Create preview branch").Click)
	waitForPath(t, page, func(u *url.URL) bool { return u.Path == databasePath+"/branches" })
	must(t, expect.Locator(page.Locator("main").GetByRole(*playwright.AriaRoleAlert)).ToContainText("database limit is reached"))

	goToOK(t, page, databasePath+"/backups")
	submitAndExpect(t, page, "/api/v1/dashboard/backups", 303, pageByRole(page, *playwright.AriaRoleButton, "Create backup").Click)
	restore := page.Locator("form.ts-db-restore").First()
	fill(t, restore.Locator(`[name="name"]`), uniqueName("restored"))
	submitAndExpect(t, page, "/api/v1/dashboard/backups/restore", 303, byRole(restore, *playwright.AriaRoleButton, "Restore side by side").Click)
	waitForPath(t, page, func(u *url.URL) bool { return u.Path == databasePath+"/backups" })
	must(t, expect.Locator(page.Locator("main").GetByRole(*playwright.AriaRoleAlert)).ToContainText("database limit is reached"))

	goToOK(t, page, "/databases")
	must(t, expect.Locator(page.Locator("#project-databases tbody tr")).ToHaveCount(1))
}

func TestDatabaseNameConflictAndDeleteConfirmationStayInTheirDialogs(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	login(t, page, sampleAccount.Email, sampleAccount.Password)

	createDatabase(t, page, "core-primary", "FRA")
	dialog := pageByRole(page, *playwright.AriaRoleDialog, "New database")
	visible(t, dialog)
	must(t, expect.Locator(dialog.GetByRole(*playwright.AriaRoleAlert)).ToContainText("Another database in this project already uses that name."))
	must(t, expect.Locator(dialog.Locator("#database-name")).ToHaveValue("core-primary"))
	must(t, expect.Locator(dialog.Locator("#database-region")).ToHaveValue("FRA"))

	name := uniqueName("e2e-delete")
	createDatabase(t, page, name, "GRU")
	waitForPath(t, page, func(u *url.URL) bool { return strings.HasPrefix(u.Path, "/databases/db_") })
	databasePath := currentPath(t, page)

	confirm := pageByRole(page, *playwright.AriaRoleDialog, "Delete "+name)
	remove := func(confirmation string) {
		t.Helper()
		fill(t, confirm.Locator(`[name="confirmName"]`), confirmation)
		submitAndExpect(t, page, "/api/v1/dashboard/databases/delete", 303, byRole(confirm, *playwright.AriaRoleButton, "Delete database").Click)
	}
	click(t, pageByRole(page, *playwright.AriaRoleButton, "Delete database"))
	remove("wrong-name")
	waitForPath(t, page, func(u *url.URL) bool { return u.Path == databasePath })
	visible(t, confirm)
	must(t, expect.Locator(confirm.GetByRole(*playwright.AriaRoleAlert)).ToContainText("The name you typed does not match."))

	remove(name)
	waitForPath(t, page, func(u *url.URL) bool { return u.Path == "/databases" })
	must(t, expect.Locator(pageByRole(page, *playwright.AriaRoleLink, name)).ToHaveCount(0))
}

func TestBackupFormsReturnToTheBackupsTabWithTheirErrors(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	registerAndOnboard(t, page, newAccount("backups"), "starter")
	createDatabase(t, page, uniqueName("orders"), "GRU")
	waitForPath(t, page, func(u *url.URL) bool { return strings.HasPrefix(u.Path, "/databases/db_") })
	backupsPath := currentPath(t, page) + "/backups"
	onBackups := func() {
		t.Helper()
		waitForPath(t, page, func(u *url.URL) bool { return u.Path == backupsPath })
		tabs := pageByRole(page, *playwright.AriaRoleNavigation, "Database sections")
		must(t, expect.Locator(byRole(tabs, *playwright.AriaRoleLink, "Backups")).ToHaveAttribute("aria-current", "page"))
	}
	alert := page.Locator("main").GetByRole(*playwright.AriaRoleAlert)

	goToOK(t, page, backupsPath)
	submitAndExpect(t, page, "/api/v1/dashboard/backups", 303, pageByRole(page, *playwright.AriaRoleButton, "Create backup").Click)
	onBackups()
	restorePoints := page.Locator("#restore-points tbody tr")
	must(t, expect.Locator(restorePoints).ToHaveCount(1))
	must(t, expect.Locator(restorePoints.First()).ToContainText(" B"))

	// A schedule that is not five cron fields is refused and kept for editing.
	policy := page.Locator(`#backup-policy form.ts-form`)
	fill(t, policy.Locator("#full-cron"), "every day")
	submitAndExpect(t, page, "/api/v1/dashboard/backups/policy", 303, byRole(policy, *playwright.AriaRoleButton, "Save policy").Click)
	onBackups()
	must(t, expect.Locator(alert).ToContainText("five cron fields"))
	must(t, expect.Locator(policy.Locator("#full-cron")).ToHaveValue("every day"))
	fill(t, policy.Locator("#full-cron"), "0 2 * * *")
	submitAndExpect(t, page, "/api/v1/dashboard/backups/policy", 303, byRole(policy, *playwright.AriaRoleButton, "Save policy").Click)
	onBackups()
	must(t, expect.Locator(alert).ToHaveCount(0))
	visible(t, page.GetByText("The saved backup policy is enabled."))

	// Restore names follow the database name rules on the server too.
	restore := page.Locator("form.ts-db-restore").First()
	name := restore.Locator(`[name="name"]`)
	_, err := name.Evaluate(`input => input.removeAttribute("pattern")`, nil)
	must(t, err)
	fill(t, name, "Bad_Name")
	submitAndExpect(t, page, "/api/v1/dashboard/backups/restore", 303, byRole(restore, *playwright.AriaRoleButton, "Restore side by side").Click)
	onBackups()
	must(t, expect.Locator(alert).ToContainText("lowercase letters, digits, and hyphens"))
	must(t, expect.Locator(name).ToHaveValue("Bad_Name"))

	restored := uniqueName("restored")
	fill(t, name, restored)
	submitAndExpect(t, page, "/api/v1/dashboard/backups/restore", 303, byRole(restore, *playwright.AriaRoleButton, "Restore side by side").Click)
	visible(t, pageByRole(page, *playwright.AriaRoleHeading, restored))
}
