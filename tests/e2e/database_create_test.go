package e2e

import (
	"net/http"
	"net/url"
	"regexp"
	"sync/atomic"
	"testing"

	"github.com/mxschmitt/playwright-go"
)

// The create dialog checks its input as it is typed, shows its progress
// from submit until the request ends, and ends on the new database's
// ready state, or reopens with the reason it failed.
func TestCreateDatabaseDialogValidatesShowsProgressAndEndsReady(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	login(t, page, sampleAccount.Email, sampleAccount.Password)
	must(t, page.SetViewportSize(1280, 800))
	goToOK(t, page, "/databases?create=1")
	dialog := pageByRole(page, *playwright.AriaRoleDialog, "New database")
	visible(t, dialog)
	box, err := dialog.BoundingBox()
	must(t, err)
	if box.Width < 800 {
		t.Fatalf("create dialog is %.0fpx wide at 1280px, want at least 800px", box.Width)
	}
	form := dialog.Locator("form")
	compare := byRole(dialog, *playwright.AriaRoleLink, "Compare plans")
	visible(t, compare)
	must(t, expect.Locator(dialog.Locator(".ts-db-plan")).ToContainText("databases in use"))
	// Another form's submission is not a create: the page shows its
	// navigation bar, but the dialog stays as it is.
	_, err = page.Locator("form.ts-db-search").Evaluate(`form => form.dispatchEvent(new Event("submit", {bubbles: true, cancelable: true}))`, nil)
	must(t, err)
	must(t, expect.Locator(page.Locator("body")).ToHaveClass(regexp.MustCompile(`ts-nav-pending`)))
	must(t, expect.Locator(dialog.Locator(".ts-db-progress")).ToBeHidden())
	must(t, expect.Locator(dialog.Locator(`[role="status"]`)).ToHaveText(""))
	goToOK(t, page, "/databases?create=1")

	// A refused name is explained once the field is left, and the message
	// clears as soon as the name is fixed.
	name := dialog.GetByLabel("Database name")
	nameError := dialog.Locator("#database-name-error")
	must(t, expect.Locator(nameError).ToBeHidden())
	fill(t, name, "Bad_Name-")
	must(t, name.Press("Tab"))
	visible(t, nameError)
	must(t, expect.Locator(name).ToHaveAttribute("aria-invalid", "true"))
	must(t, expect.Locator(name).ToHaveAttribute("aria-describedby", "database-name-error database-name-hint"))
	fill(t, name, uniqueName("progress"))
	must(t, expect.Locator(nameError).ToBeHidden())
	must(t, expect.Locator(name).Not().ToHaveAttribute("aria-invalid", "true"))

	// Regions are cards; none checked is explained, and the first checked
	// region is the primary one.
	regionError := dialog.Locator(".ts-db-regions .ts-field-error")
	selectDatabaseRegions(t, dialog)
	must(t, expect.Locator(regionError).ToContainText("Select one or more"))
	selectDatabaseRegions(t, dialog, "GRU", "SFO")
	must(t, expect.Locator(regionError).ToBeHidden())
	visible(t, dialog.Locator(`.ts-db-region-card:has(input[value="GRU"]) .ts-db-region-primary`))
	must(t, expect.Locator(dialog.Locator(`.ts-db-region-card:has(input[value="SFO"]) .ts-db-region-primary`)).ToBeHidden())
	expectAccessible(t, page, "new database dialog", "")
	screenshot(t, page, "ts753/create-dialog")

	// A 204 keeps the document, so the pending state can be inspected
	// while the browser still sends its real native POST.
	var posts atomic.Int32
	must(t, page.Route("**/api/v1/dashboard/databases", func(route playwright.Route) {
		posts.Add(1)
		_ = route.Fulfill(playwright.RouteFulfillOptions{Status: new(http.StatusNoContent)})
	}))
	progress := dialog.Locator(".ts-db-progress")
	bar := progress.Locator(".ts-db-progress-bar")
	must(t, expect.Locator(progress).ToBeHidden())
	click(t, byRole(form, *playwright.AriaRoleButton, "Create database"))
	visible(t, progress)
	must(t, expect.Locator(progress).ToContainText("Creating database"))
	must(t, expect.Locator(dialog.Locator(`[role="status"]`)).ToHaveText("Creating database. It opens as soon as it is ready."))
	// The covered form is inert; focus moves to the close button.
	must(t, expect.Locator(form).ToHaveAttribute("inert", ""))
	must(t, expect.Locator(byRole(dialog.Locator(".ts-dialog-head"), *playwright.AriaRoleButton, "Close")).ToBeFocused())
	must(t, expect.Locator(bar).ToHaveCSS("animation-name", "ts-progress-shine"))
	viewportScreenshot(t, page, "ts753/create-progress")
	// data-submit-once: a second submission is not sent.
	_, err = form.Evaluate(`form => form.requestSubmit()`, nil)
	must(t, err)
	eventually(t, "the create post", func() bool { return posts.Load() >= 1 })
	if count := posts.Load(); count != 1 {
		t.Fatalf("sent %d create posts, want 1", count)
	}
	// Reduced motion keeps the progress panel but holds it still.
	must(t, page.EmulateMedia(playwright.PageEmulateMediaOptions{ReducedMotion: playwright.ReducedMotionReduce}))
	visible(t, progress)
	must(t, expect.Locator(bar).ToHaveCSS("animation-name", "none"))
	must(t, page.EmulateMedia(playwright.PageEmulateMediaOptions{ReducedMotion: playwright.ReducedMotionNoPreference}))
	must(t, page.Keyboard().Press("Escape"))
	must(t, expect.Locator(progress).ToBeHidden())
	// The dialog closed with Escape, so find the form by its id.
	must(t, expect.Locator(page.Locator("#create-database form")).Not().ToHaveAttribute("inert", ""))
	must(t, page.Unroute("**/api/v1/dashboard/databases"))

	// The real create opens the database on its ready state.
	created := uniqueName("ready")
	createDatabase(t, page, created, "GRU")
	waitForPath(t, page, func(u *url.URL) bool { return entityPath(u.Path, "/databases/") })
	ready := page.Locator(".ts-db-created")
	must(t, expect.Locator(ready).ToHaveAttribute("data-status", "ready"))
	must(t, expect.Locator(ready).ToContainText(created+" is ready"))
	must(t, expect.Locator(ready).ToHaveCSS("animation-name", "ts-pop-in"))
	viewportScreenshot(t, page, "ts753/create-ready")
	databasePath := currentPath(t, page)
	// Phone widths stack the list, and its column headers are still read.
	must(t, page.SetViewportSize(390, 844))
	goToOK(t, page, "/databases")
	for _, header := range []string{"Name", "Status", "Regions"} {
		must(t, expect.Locator(pageByRole(page, *playwright.AriaRoleColumnheader, header)).ToHaveCount(1))
	}
	must(t, page.SetViewportSize(1280, 800))
	goToOK(t, page, databasePath)
	// The notice belongs to the first view only.
	goToOK(t, page, currentPath(t, page))
	must(t, expect.Locator(page.Locator(".ts-db-created")).ToHaveCount(0))

	// A refused create reopens the dialog, without progress, saying why.
	createDatabase(t, page, created, "SFO")
	dialog = pageByRole(page, *playwright.AriaRoleDialog, "New database")
	visible(t, dialog)
	must(t, expect.Locator(dialog.Locator(".ts-db-progress")).ToBeHidden())
	alert := dialog.GetByRole(*playwright.AriaRoleAlert).First()
	must(t, expect.Locator(alert).ToContainText("Database not created"))
	must(t, expect.Locator(alert).ToContainText("Another database in this project already uses that name."))
	must(t, expect.Locator(dialog.Locator("#database-name")).ToHaveValue(created))
	viewportScreenshot(t, page, "ts753/create-failed")
}
