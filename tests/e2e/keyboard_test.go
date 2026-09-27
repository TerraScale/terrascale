package e2e

import (
	"net/url"
	"slices"
	"testing"

	"github.com/mxschmitt/playwright-go"
)

func TestKeyboardLineChartIsOneTabStopWalkedWithArrowKeys(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	login(t, page, sampleAccount.Email, sampleAccount.Password)
	goToOK(t, page, "/")

	chart := page.Locator("svg.ts-chart")
	columns := chart.Locator(".ts-chart-column")
	count, err := columns.Count()
	must(t, err)
	if count < 3 {
		t.Fatalf("activity chart has %d columns, want at least 3", count)
	}
	must(t, expect.Locator(chart.Locator(`.ts-chart-column[tabindex="0"]`)).ToHaveCount(1))

	// Tab enters the chart on the latest point and a second Tab leaves it.
	must(t, page.Locator(".ts-chart-shell").Focus())
	must(t, page.Keyboard().Press("Tab"))
	must(t, expect.Locator(columns.Last()).ToBeFocused())
	for _, step := range []struct {
		key    string
		column playwright.Locator
	}{
		{"ArrowLeft", columns.Nth(count - 2)},
		{"ArrowRight", columns.Last()},
		{"ArrowRight", columns.Last()},
		{"Home", columns.First()},
		{"ArrowLeft", columns.First()},
		{"ArrowRight", columns.Nth(1)},
	} {
		must(t, page.Keyboard().Press(step.key))
		must(t, expect.Locator(step.column).ToBeFocused())
	}
	must(t, expect.Locator(columns.Nth(1)).ToHaveAttribute("tabindex", "0"))
	must(t, expect.Locator(chart.Locator(`.ts-chart-column[tabindex="0"]`)).ToHaveCount(1))

	// The tab stop stays on the last visited point.
	must(t, page.Keyboard().Press("Shift+Tab"))
	must(t, expect.Locator(page.Locator(".ts-chart-shell")).ToBeFocused())
	must(t, page.Keyboard().Press("Tab"))
	must(t, expect.Locator(columns.Nth(1)).ToBeFocused())
	must(t, page.Keyboard().Press("End"))
	must(t, expect.Locator(columns.Last()).ToBeFocused())
	must(t, page.Keyboard().Press("Tab"))
	must(t, expect.Locator(pageByRole(page, *playwright.AriaRoleLink, "View persisted audit events →")).ToBeFocused())

	// Without JavaScript every point stays reachable with Tab.
	native := watchedPage(t, cloneSession(t, page.Context(), false))
	goToOK(t, native, "/")
	must(t, expect.Locator(native.Locator(`svg.ts-chart .ts-chart-column[tabindex="0"]`)).ToHaveCount(count))
}

func TestKeyboardMenusShowFocusAndKeepItAfterAPatch(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	login(t, page, sampleAccount.Email, sampleAccount.Password)
	goToOK(t, page, "/access?tab=members")

	// The account trigger is named by the text it shows.
	account := page.Locator("details.ts-account > summary")
	must(t, expect.Locator(account).ToHaveAccessibleName("Account menu for "+sampleAccount.DisplayName+" "+sampleAccount.Email))

	// A focused menu item draws the solid focus ring, unlike hover and the
	// current item.
	must(t, account.Focus())
	must(t, page.Keyboard().Press("Enter"))
	must(t, page.Keyboard().Press("Tab"))
	profile := byRole(page.Locator("details.ts-account"), *playwright.AriaRoleLink, "Profile")
	must(t, expect.Locator(profile).ToBeFocused())
	must(t, expect.Locator(profile).ToHaveCSS("outline-style", "solid"))
	must(t, expect.Locator(profile).ToHaveCSS("outline-width", "2px"))
	must(t, page.Keyboard().Press("Escape"))
	must(t, expect.Locator(account).ToBeFocused())

	// Choosing a role patches the member list through Datastar; focus returns
	// to the closed menu's trigger instead of falling back to the page.
	roles := page.Locator("#access-filter-form details.ts-menu")
	trigger := roles.Locator("summary")
	must(t, trigger.Focus())
	must(t, page.Keyboard().Press("Enter"))
	must(t, expect.Locator(roles).ToHaveAttribute("open", ""))
	owner := byRole(roles, *playwright.AriaRoleButton, "Organization owner")
	for range 5 {
		if focused, err := owner.Evaluate("el => el === document.activeElement", nil); err == nil && focused == true {
			break
		}
		must(t, page.Keyboard().Press("Tab"))
	}
	must(t, expect.Locator(owner).ToBeFocused())
	_, err := page.ExpectResponse(func(response playwright.Response) bool {
		parsed, err := url.Parse(response.URL())
		return err == nil && parsed.Path == "/access" && slices.Contains(parsed.Query()["role"], "owner") &&
			response.Request().Headers()["datastar-request"] == "true"
	}, func() error { return page.Keyboard().Press("Enter") })
	must(t, err)
	must(t, expect.Locator(trigger).ToContainText("Organization owner"))
	must(t, expect.Locator(roles).Not().ToHaveAttribute("open", ""))
	must(t, expect.Locator(trigger).ToBeFocused())

	// A click outside still closes an open menu.
	must(t, page.Keyboard().Press("Enter"))
	must(t, expect.Locator(roles).ToHaveAttribute("open", ""))
	must(t, page.Locator("#ts-main").Click(playwright.LocatorClickOptions{Position: &playwright.Position{X: 4, Y: 4}}))
	must(t, expect.Locator(roles).Not().ToHaveAttribute("open", ""))
}
