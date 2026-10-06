package e2e

import (
	"github.com/mxschmitt/playwright-go"
	"testing"
)

func TestOrganizationBillingTabsAndLegacyRedirect(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	login(t, page, sampleAccount.Email, sampleAccount.Password)
	goToOK(t, page, "/billing?interval=annual")
	if currentPath(t, page) != "/organization" {
		t.Fatalf("legacy billing did not redirect: %s", page.URL())
	}
	tabs := pageByRole(page, *playwright.AriaRoleNavigation, "Organization sections")
	must(t, expect.Locator(tabs.Locator("a")).ToHaveText([]string{"Organization", "Members", "Invitations", "Projects", "Billing"}))
	must(t, expect.Locator(byRole(tabs, *playwright.AriaRoleLink, "Billing", true)).ToHaveAttribute("aria-current", "page"))
	visible(t, pageByRole(page, *playwright.AriaRoleHeading, "Available plans"))
	visible(t, pageByRole(page, *playwright.AriaRoleHeading, "Payment history"))
	must(t, expect.Locator(page.Locator("#ts-navigation a[href='/billing']")).ToHaveCount(0))
	click(t, byRole(tabs, *playwright.AriaRoleLink, "Projects", true))
	visible(t, pageByRole(page, *playwright.AriaRoleHeading, "Projects", true))
	must(t, expect.Locator(page.Locator("#billing-summary")).ToHaveCount(0))
	click(t, byRole(tabs, *playwright.AriaRoleLink, "Members", true))
	visible(t, pageByRole(page, *playwright.AriaRoleHeading, "Organization members", true))
	must(t, expect.Locator(page.Locator("#billing-summary")).ToHaveCount(0))
	goToOK(t, page, "/access")
	if currentPath(t, page) != "/organization" {
		t.Fatalf("legacy team did not redirect: %s", page.URL())
	}
	must(t, expect.Locator(byRole(tabs, *playwright.AriaRoleLink, "Members", true)).ToHaveAttribute("aria-current", "page"))
	visible(t, page.Locator("#member-search"))
	goToOK(t, page, "/access?tab=invitations&create=1")
	must(t, expect.Locator(byRole(tabs, *playwright.AriaRoleLink, "Invitations", true)).ToHaveAttribute("aria-current", "page"))
	visible(t, page.Locator("#invite-email"))
	goToOK(t, page, "/access?create=1")
	must(t, expect.Locator(byRole(tabs, *playwright.AriaRoleLink, "Invitations", true)).ToHaveAttribute("aria-current", "page"))
	visible(t, page.Locator("#invite-email"))
	click(t, byRole(tabs, *playwright.AriaRoleLink, "Organization", true))
	visible(t, pageByRole(page, *playwright.AriaRoleHeading, "Organization details", true))
	must(t, expect.Locator(page.Locator("#billing-summary")).ToHaveCount(0))
}
