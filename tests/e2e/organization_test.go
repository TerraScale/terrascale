package e2e

import (
	"net/url"
	"slices"
	"testing"

	"github.com/mxschmitt/playwright-go"
)

func TestOrganizationSectionsKeepTheirActions(t *testing.T) {
	page := newPage(t)
	login(t, page, sampleAccount.Email, sampleAccount.Password)
	goToOK(t, page, "/organization")
	tabs := pageByRole(page, *playwright.AriaRoleNavigation, "Organization sections")
	must(t, expect.Locator(tabs.Locator("a")).ToHaveText([]string{"Organization", "Members", "Projects"}))
	must(t, expect.Locator(tabs.Locator("[aria-current=page]")).ToHaveText("Organization"))
	visible(t, page.Locator("#organization-details"))
	visible(t, page.Locator("#organization-ownership"))
	visible(t, pageByRole(page, *playwright.AriaRoleButton, "Edit name", true))
	visible(t, pageByRole(page, *playwright.AriaRoleLink, "Create organization", true))
	must(t, expect.Locator(page.Locator("main #organization-switch, main [name=organizationId], main #team-members, main #projects")).ToHaveCount(0))
	must(t, expect.Locator(page.Locator("[data-organization-option]")).Not().ToHaveCount(0))

	for _, width := range []int{1440, 390} {
		must(t, page.SetViewportSize(width, 900))
		click(t, byRole(tabs, *playwright.AriaRoleLink, "Members", true))
		must(t, expect.Locator(tabs.Locator("[aria-current=page]")).ToHaveText("Members"))
		visible(t, page.Locator("#team-members"))
		visible(t, page.Locator("#team-invitations"))
		visible(t, page.Locator("#member-search"))
		visible(t, page.Locator("#invite-email"))
		must(t, expect.Locator(page.Locator("#organization-details, #organization-ownership, #projects, a[href='/organization?newOrganization=organization']")).ToHaveCount(0))
		fill(t, page.Locator("#member-search"), "no-such-member")
		must(t, page.Locator("#member-search").Press("Enter"))
		visible(t, page.GetByText("No members returned", playwright.PageGetByTextOptions{Exact: new(true)}))
		visible(t, page.Locator("#team-invitations"))
		click(t, byRole(tabs, *playwright.AriaRoleLink, "Projects", true))
		must(t, expect.Locator(tabs.Locator("[aria-current=page]")).ToHaveText("Projects"))
		visible(t, page.Locator("#projects"))
		visible(t, page.Locator(`button[command="show-modal"][commandfor="create-project"]`))
		must(t, expect.Locator(page.Locator("#organization-details, #organization-ownership, #team-members, #team-invitations, #member-search")).ToHaveCount(0))
		if currentPath(t, page) != "/organization" {
			t.Fatalf("tab navigation left Organization: %s", page.URL())
		}
		_, err := page.Reload()
		must(t, err)
		must(t, expect.Locator(tabs.Locator("[aria-current=page]")).ToHaveText("Projects"))
	}
	must(t, page.SetViewportSize(1440, 900))
	selectButton := page.Locator("#projects").GetByRole(*playwright.AriaRoleButton, playwright.LocatorGetByRoleOptions{Name: "Select", Exact: new(true)}).First()
	submitAndExpect(t, page, "/api/v1/dashboard/switch-project", 303, selectButton.Click)
	waitForPath(t, page, func(u *url.URL) bool { return u.Path == "/organization" && u.Query().Get("tab") == "projects" })
	must(t, expect.Locator(tabs.Locator("[aria-current=page]")).ToHaveText("Projects"))
	// Restore the fixture's selected project for the remaining serial tests.
	coreProject := page.Locator("#projects tbody tr").Filter(playwright.LocatorFilterOptions{HasText: "core-platform"})
	submitAndExpect(t, page, "/api/v1/dashboard/switch-project", 303, byRole(coreProject, *playwright.AriaRoleButton, "Select", true).Click)
}

// TestOwnershipTransferIsConfirmedAndCanBeCanceled requests a transfer in the
// sample organization and cancels it again. It stays serial so no parallel
// test sees the sample organization mid-transfer.
func TestOwnershipTransferIsConfirmedAndCanBeCanceled(t *testing.T) {
	const ravi = "usr_01JC9S9V6Q1X4Z6B8D0F3H6JN"
	page := newPage(t)
	login(t, page, sampleAccount.Email, sampleAccount.Password)
	goToOK(t, page, "/organization")
	switchContext(t, page, "data-organization-option", "TerraScale Labs")

	ownership := page.Locator("#organization-ownership")
	visible(t, ownership.GetByText("As the owner you can't leave this organization."))
	click(t, byRole(ownership, *playwright.AriaRoleButton, "Transfer ownership"))
	dialog := page.Locator("dialog#transfer-ownership")
	visible(t, dialog)
	selectOption(t, dialog.Locator(`[name="userID"]`), ravi)
	submitAndExpect(t, page, "/api/v1/dashboard/organization/members/ownership", 303, byRole(dialog, *playwright.AriaRoleButton, "Request transfer").Click)

	pending := ownership.GetByRole(*playwright.AriaRoleStatus).Filter(playwright.LocatorFilterOptions{HasText: "Ownership transfer pending"})
	must(t, expect.Locator(pending).ToContainText("Waiting for Ravi Patel (ravi@terrascale.tech) to accept."))
	must(t, expect.Locator(byRole(ownership, *playwright.AriaRoleButton, "Transfer ownership")).ToHaveCount(0))
	submitAndExpect(t, page, "/api/v1/dashboard/organization/members/ownership/cancel", 303, byRole(pending, *playwright.AriaRoleButton, "Cancel transfer").Click)
	must(t, expect.Locator(pending).ToHaveCount(0))
	visible(t, byRole(ownership, *playwright.AriaRoleButton, "Transfer ownership"))
}

// TestProjectDialogRegionLabelsStayInsideMapsApart opens both project dialogs
// at the desktop size of TS-415 with every region allowed: each shown label
// must sit inside its map, clear of the other labels, flags and hubs.
func TestProjectDialogRegionLabelsStayInsideMapsApart(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	must(t, page.SetViewportSize(1680, 1000))
	login(t, page, sampleAccount.Email, sampleAccount.Password)
	goToOK(t, page, "/organization?tab=projects")
	all := []string{"SFO", "GRU", "FRA"}

	click(t, page.Locator(`button[command="show-modal"][commandfor="create-project"]`))
	create := page.Locator("dialog#create-project")
	// Hovering a pin shows only its label, so keep the pointer off the maps.
	must(t, page.Mouse().Move(0, 0))
	expectRegionLabels(t, create.Locator(`.ts-region-map[data-select-id="project-regions"]`), all...)
	expectRegionLabelsClear(t, create)
	must(t, page.Keyboard().Press("Escape"))

	click(t, page.Locator("tbody tr").Filter(playwright.LocatorFilterOptions{HasText: "core-platform"}).GetByRole(*playwright.AriaRoleButton, playwright.LocatorGetByRoleOptions{Name: "Rename"}))
	edit := pageByRole(page, *playwright.AriaRoleDialog, "Edit core-platform")
	allowed := edit.Locator(`.ts-region-map[data-select-id^="project-regions-"]`)
	must(t, expect.Locator(allowed).ToHaveAttribute("data-selected", "SFO"))
	click(t, allowed.Locator(`button[data-region="GRU"]`))
	click(t, allowed.Locator(`button[data-region="FRA"]`))
	must(t, page.Mouse().Move(0, 0))
	expectRegionLabels(t, allowed, all...)
	expectRegionLabelsClear(t, edit)

	backup := edit.Locator(`.ts-region-map[data-select-id^="project-backup-"]`)
	for _, code := range all {
		must(t, backup.Locator(`button[data-region="`+code+`"]`).Hover())
		expectRegionLabels(t, backup, code)
		expectRegionLabelsClear(t, edit)
	}
}

// expectRegionLabels waits until regionMap shows exactly the labels of codes.
func expectRegionLabels(t *testing.T, regionMap playwright.Locator, codes ...string) {
	t.Helper()
	for _, code := range []string{"SFO", "GRU", "FRA"} {
		opacity := "0"
		if slices.Contains(codes, code) {
			opacity = "1"
		}
		must(t, expect.Locator(regionMap.Locator(`button[data-region="`+code+`"] .ts-region-node-label`)).ToHaveCSS("opacity", opacity))
	}
}

// expectRegionLabelsClear fails when a shown label of a region map in scope
// leaves its map or covers another label, a flag, or a hub.
func expectRegionLabelsClear(t *testing.T, scope playwright.Locator) {
	t.Helper()
	type rect = playwright.Rect
	overlaps := func(a, b *rect) bool {
		return a.X < b.X+b.Width && b.X < a.X+a.Width && a.Y < b.Y+b.Height && b.Y < a.Y+a.Height
	}
	// The dialog scales in; boxes read mid-animation are off by a few pixels.
	eventually(t, "dialog animation ends", func() bool {
		done, err := scope.Evaluate(`el => el.getAnimations().length === 0`, nil)
		return err == nil && done == true
	})
	maps, err := scope.Locator(".ts-region-map[data-select-id]").All()
	must(t, err)
	for i, regionMap := range maps {
		bounds, err := regionMap.BoundingBox()
		must(t, err)
		var labels, obstacles []*rect
		for _, code := range []string{"SFO", "GRU", "FRA"} {
			for _, part := range []string{".ts-region-node-flag", ".ts-region-node-core"} {
				box, err := regionMap.Locator(`button[data-region="` + code + `"] ` + part).BoundingBox()
				must(t, err)
				obstacles = append(obstacles, box)
			}
		}
		for _, code := range []string{"SFO", "GRU", "FRA"} {
			label := regionMap.Locator(`button[data-region="` + code + `"] .ts-region-node-label`)
			opacity, err := label.Evaluate(`el => getComputedStyle(el).opacity`, nil)
			must(t, err)
			if opacity == "0" {
				continue
			}
			box, err := label.BoundingBox()
			must(t, err)
			if box.X < bounds.X || box.Y < bounds.Y || box.X+box.Width > bounds.X+bounds.Width || box.Y+box.Height > bounds.Y+bounds.Height {
				t.Fatalf("map %d: %s label %+v extends past map %+v", i, code, box, bounds)
			}
			for _, other := range append(labels, obstacles...) {
				if overlaps(box, other) {
					t.Fatalf("map %d: %s label %+v overlaps %+v", i, code, box, other)
				}
			}
			labels = append(labels, box)
		}
	}
}
