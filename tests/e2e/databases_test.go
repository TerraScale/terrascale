package e2e

import (
	"net/url"
	"slices"
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
	selectDatabaseRegions(t, form, region)
	submitAndExpect(t, page, "/api/v1/dashboard/databases", 303, byRole(form, *playwright.AriaRoleButton, "Create database").Click)
}

// selectDatabaseRegions sets the region cards to exactly regions, by
// keyboard, the way the native no-script controls are used.
func selectDatabaseRegions(t *testing.T, scope playwright.Locator, regions ...string) {
	t.Helper()
	for _, code := range []string{"GRU", "SFO"} {
		checkbox := scope.Locator(`#database-region input[value="` + code + `"]`)
		checked, err := checkbox.IsChecked()
		must(t, err)
		if checked != slices.Contains(regions, code) {
			must(t, checkbox.Press("Space"))
		}
		if slices.Contains(regions, code) {
			must(t, expect.Locator(checkbox).ToBeChecked())
		} else {
			must(t, expect.Locator(checkbox).Not().ToBeChecked())
		}
	}
}

func TestCreateDatabaseRegionMapAndSelectStayInSync(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	login(t, page, sampleAccount.Email, sampleAccount.Password)
	goToOK(t, page, "/databases?create=1")
	dialog := pageByRole(page, *playwright.AriaRoleDialog, "New database")
	region := dialog.Locator("#database-region")
	regionMap := dialog.Locator(`.ts-region-map[data-select-id="database-region"]`)
	sanFrancisco := regionMap.Locator(`button[data-region="SFO"]`)
	saoPaulo := regionMap.Locator(`button[data-region="GRU"]`)
	// The picker offers what ts-atlas discovery lists, and nothing else.
	must(t, expect.Locator(region.Locator(`input[name="region"]`)).ToHaveCount(2))
	must(t, expect.Locator(regionMap.Locator(`button[data-region]`)).ToHaveCount(2))
	must(t, expect.Locator(dialog.Locator(`[value="FRA"], [data-region="FRA"]`)).ToHaveCount(0))
	must(t, expect.Locator(region.Locator(`input[value="SFO"]`)).ToBeChecked())
	selectDatabaseRegions(t, dialog, "GRU")

	click(t, sanFrancisco)
	must(t, expect.Locator(region.Locator(`input[value="GRU"]`)).ToBeChecked())
	must(t, expect.Locator(region.Locator(`input[value="SFO"]`)).ToBeChecked())
	must(t, expect.Locator(regionMap).ToHaveAttribute("data-selected", "GRU SFO"))
	must(t, expect.Locator(sanFrancisco).ToHaveAttribute("aria-pressed", "true"))
	visible(t, sanFrancisco.Locator(".ts-region-node-label"))
	must(t, expect.Locator(sanFrancisco.Locator(".ts-region-node-label")).ToHaveCSS("opacity", "1"))
	must(t, expect.Locator(sanFrancisco.Locator(".ts-region-node-label")).ToContainText("San Francisco"))
	must(t, expect.Locator(sanFrancisco.Locator(".ts-region-node-flag")).ToHaveAttribute("src", assetURL("/flags/us.svg")))
	expectAccessible(t, page, "new database region map", "")
	screenshot(t, page, "database-region-selected")

	selectDatabaseRegions(t, dialog, "SFO")
	must(t, expect.Locator(region.Locator(`input[value="GRU"]`)).Not().ToBeChecked())
	must(t, expect.Locator(regionMap).ToHaveAttribute("data-selected", "SFO"))
	must(t, expect.Locator(sanFrancisco).ToHaveAttribute("aria-pressed", "true"))
	must(t, expect.Locator(saoPaulo).ToHaveAttribute("aria-pressed", "false"))
	screenshot(t, page, "database-region-cards")
	must(t, saoPaulo.Hover())
	must(t, expect.Locator(saoPaulo.Locator(".ts-region-node-label")).ToHaveCSS("opacity", "1"))

	must(t, page.SetViewportSize(320, 700))
	mapBounds, err := regionMap.BoundingBox()
	must(t, err)
	for _, code := range []string{"SFO", "GRU"} {
		pin := regionMap.Locator(`button[data-region="` + code + `"]`)
		must(t, pin.Hover())
		labelBounds, err := pin.Locator(".ts-region-node-label").BoundingBox()
		must(t, err)
		if labelBounds.X < mapBounds.X || labelBounds.X+labelBounds.Width > mapBounds.X+mapBounds.Width {
			t.Fatalf("%s label bounds %+v extend past map bounds %+v at 320px", code, labelBounds, mapBounds)
		}
	}

	must(t, page.SetViewportSize(1280, 800))
	click(t, sanFrancisco)
	must(t, saoPaulo.Hover())
	must(t, expect.Locator(sanFrancisco.Locator(".ts-region-node-label")).ToHaveCSS("opacity", "0"))
	must(t, expect.Locator(saoPaulo.Locator(".ts-region-node-label")).ToHaveCSS("opacity", "1"))
}

// One database per placement option: GRU only, SFO only, and both, which
// replicates. The detail page shows where each lives, and its placement
// form moves a database between the options.
func TestDatabasePlacementIsGRUOnlySFOOnlyOrBoth(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	login(t, page, sampleAccount.Email, sampleAccount.Password)
	for _, placement := range []struct {
		regions     []string
		replication string
	}{
		{[]string{"GRU"}, "Single region · GRU only"},
		{[]string{"SFO"}, "Single region · SFO only"},
		{[]string{"GRU", "SFO"}, "Replicated across GRU, SFO"},
	} {
		goToOK(t, page, "/databases?create=1")
		dialog := pageByRole(page, *playwright.AriaRoleDialog, "New database")
		selectDatabaseRegions(t, dialog, placement.regions...)
		fill(t, dialog.Locator("#database-name"), uniqueName("placement"))
		submitAndExpect(t, page, "/api/v1/dashboard/databases", 303, byRole(dialog, *playwright.AriaRoleButton, "Create database").Click)
		waitForPath(t, page, func(u *url.URL) bool { return entityPath(u.Path, "/databases/") })
		card := page.Locator("#placement")
		must(t, expect.Locator(card).ToContainText(placement.replication))
		form := card.Locator(`form[action="/api/v1/dashboard/databases/regions"]`)
		for _, code := range []string{"GRU", "SFO"} {
			checkbox := form.Locator(`input[value="` + code + `"]`)
			if slices.Contains(placement.regions, code) {
				must(t, expect.Locator(checkbox).ToBeChecked())
			} else {
				must(t, expect.Locator(checkbox).Not().ToBeChecked())
			}
		}
	}
	screenshot(t, page, "database-placement-replicated")

	// The replicated database keeps GRU, its home region, and drops SFO.
	card := page.Locator("#placement")
	form := card.Locator(`form[action="/api/v1/dashboard/databases/regions"]`)
	must(t, expect.Locator(form.Locator(`input[value="GRU"]`)).ToHaveAttribute("required", ""))
	must(t, expect.Locator(form.Locator(`input[value="SFO"]`)).Not().ToHaveAttribute("required", ""))
	must(t, expect.Locator(card).ToContainText("GRU · São Paulo is the home region"))
	must(t, form.Locator(`input[value="SFO"]`).Uncheck())
	submitAndExpect(t, page, "/api/v1/dashboard/databases/regions", 303, byRole(form, *playwright.AriaRoleButton, "Save regions").Click)
	must(t, expect.Locator(card).ToContainText("Single region · GRU only"))
}

func TestCreateDatabaseRegionsWorkWithoutScripts(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	registerAndOnboard(t, page, newAccount("db-regions-native"))
	nativePage := watchedPage(t, cloneSession(t, page.Context(), false))
	goToOK(t, nativePage, "/databases")
	click(t, pageByRole(nativePage, *playwright.AriaRoleButton, "New database").First())
	dialog := pageByRole(nativePage, *playwright.AriaRoleDialog, "New database")
	selectDatabaseRegions(t, dialog, "GRU", "SFO")
	must(t, expect.Locator(dialog.Locator(`#database-region input[value="GRU"]`)).ToBeChecked())
	must(t, expect.Locator(dialog.Locator(`#database-region input[value="SFO"]`)).ToBeChecked())
	fill(t, dialog.Locator("#database-name"), uniqueName("native-regions"))
	// Native keyboard submission avoids waiting for the animated dialog's bounds to stabilize.
	submitAndExpect(t, nativePage, "/api/v1/dashboard/databases", 303, func(...playwright.LocatorClickOptions) error {
		return byRole(dialog, *playwright.AriaRoleButton, "Create database").Press("Enter")
	})
	waitForPath(t, nativePage, func(u *url.URL) bool { return entityPath(u.Path, "/databases/") })
	must(t, expect.Locator(nativePage.Locator("main")).ToContainText("GRU · São Paulo"))
	must(t, expect.Locator(nativePage.Locator("main")).ToContainText("SFO · San Francisco"))
	must(t, expect.Locator(nativePage.Locator("#placement")).ToContainText("Replicated across GRU, SFO"))
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
	waitForPath(t, page, func(u *url.URL) bool { return entityPath(u.Path, "/databases/") })
	databasePath := currentPath(t, page)

	// The Free plan's one database is in use: the dialog says so up front,
	// and a refused create keeps the name and region.
	second := uniqueName("second")
	createDatabase(t, page, second, "SFO")
	dialog := pageByRole(page, *playwright.AriaRoleDialog, "New database")
	visible(t, dialog)
	must(t, expect.Locator(dialog.GetByRole(*playwright.AriaRoleAlert)).ToContainText("The Free plan includes 1 database"))
	must(t, expect.Locator(byRole(dialog, *playwright.AriaRoleLink, "Compare plans")).ToHaveAttribute("href", "/organization?tab=billing"))
	must(t, expect.Locator(dialog.Locator("#database-name")).ToHaveValue(second))
	must(t, expect.Locator(dialog.Locator(`#database-region input[value="SFO"]`)).ToBeChecked())

	// Branches and restores count against the same limit.
	goToOK(t, page, databasePath+"/branches")
	submitAndExpect(t, page, "/api/v1/dashboard/databases/branch", 303, pageByRole(page, *playwright.AriaRoleButton, "Create preview branch").Click)
	waitForPath(t, page, func(u *url.URL) bool { return u.Path == databasePath+"/branches" })
	must(t, expect.Locator(page.Locator("main").GetByRole(*playwright.AriaRoleAlert)).ToContainText("database limit is reached"))

	goToOK(t, page, databasePath+"/backups")
	submitAndExpect(t, page, "/api/v1/dashboard/backups", 303, pageByRole(page, *playwright.AriaRoleButton, "Create backup").Click)
	click(t, pageByRole(page, *playwright.AriaRoleButton, "Restore", true).First())
	restore := pageByRole(page, *playwright.AriaRoleDialog, "Restore backup")
	fill(t, restore.Locator(`[name="name"]`), uniqueName("restored"))
	submitAndExpect(t, page, "/api/v1/dashboard/backups/restore", 303, byRole(restore, *playwright.AriaRoleButton, "Restore as new database").Click)
	waitForPath(t, page, func(u *url.URL) bool { return u.Path == databasePath+"/backups" })
	visible(t, restore.GetByText("database limit is reached"))

	goToOK(t, page, "/databases")
	must(t, expect.Locator(page.Locator("#project-databases tbody tr")).ToHaveCount(1))
}

func TestDatabaseNameConflictAndDeleteConfirmationStayInTheirDialogs(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	login(t, page, sampleAccount.Email, sampleAccount.Password)

	createDatabase(t, page, "core-primary", "SFO")
	dialog := pageByRole(page, *playwright.AriaRoleDialog, "New database")
	visible(t, dialog)
	must(t, expect.Locator(dialog.GetByRole(*playwright.AriaRoleAlert)).ToContainText("Another database in this project already uses that name."))
	must(t, expect.Locator(dialog.Locator("#database-name")).ToHaveValue("core-primary"))
	must(t, expect.Locator(dialog.Locator(`#database-region input[value="SFO"]`)).ToBeChecked())

	name := uniqueName("e2e-delete")
	createDatabase(t, page, name, "GRU")
	waitForPath(t, page, func(u *url.URL) bool { return entityPath(u.Path, "/databases/") })
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
	database := uniqueName("orders")
	createDatabase(t, page, database, "GRU")
	waitForPath(t, page, func(u *url.URL) bool { return entityPath(u.Path, "/databases/") })
	databasePath := currentPath(t, page)
	backupsPath := databasePath + "/backups"
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

	// Overwriting the database takes its name typed exactly; a mismatch
	// reopens the dialog with the error.
	restore := pageByRole(page, *playwright.AriaRoleDialog, "Restore backup")
	openRestore := func() {
		t.Helper()
		click(t, byRole(restorePoints.First(), *playwright.AriaRoleButton, "Restore", true))
		visible(t, restore)
	}
	overwrite := func(confirmation string) {
		t.Helper()
		fill(t, restore.Locator(`[name="confirmName"]`), confirmation)
		submitAndExpect(t, page, "/api/v1/dashboard/backups/restore", 303, byRole(restore, *playwright.AriaRoleButton, "Overwrite "+database).Click)
	}
	openRestore()
	must(t, expect.Locator(restore).ToContainText("This overwrites current data"))
	overwrite("wrong-name")
	onBackups()
	visible(t, restore)
	visible(t, restore.GetByText("The name you typed does not match."))
	overwrite(database)
	waitForPath(t, page, func(u *url.URL) bool { return u.Path == databasePath })

	// Restore names follow the database name rules on the server too.
	goToOK(t, page, backupsPath)
	openRestore()
	name := restore.Locator(`[name="name"]`)
	_, err := name.Evaluate(`input => input.removeAttribute("pattern")`, nil)
	must(t, err)
	fill(t, name, "Bad_Name")
	submitAndExpect(t, page, "/api/v1/dashboard/backups/restore", 303, byRole(restore, *playwright.AriaRoleButton, "Restore as new database").Click)
	onBackups()
	visible(t, restore.GetByText("lowercase letters, digits, and hyphens"))
	must(t, expect.Locator(name).ToHaveValue("Bad_Name"))

	restored := uniqueName("restored")
	fill(t, name, restored)
	submitAndExpect(t, page, "/api/v1/dashboard/backups/restore", 303, byRole(restore, *playwright.AriaRoleButton, "Restore as new database").Click)
	visible(t, pageByRole(page, *playwright.AriaRoleHeading, restored))
}
