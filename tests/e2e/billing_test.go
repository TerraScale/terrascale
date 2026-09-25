package e2e

import (
	"net/url"
	"regexp"
	"testing"

	"github.com/mxschmitt/playwright-go"
)

func TestDemoBillingUpgradesThroughLocalSimulator(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	registerAndOnboard(t, page, newAccount("billing"))

	response := goTo(t, page, "/billing")
	if response.Status() != 200 || currentPath(t, page) != "/billing" {
		t.Fatalf("GET /billing = %d at %s", response.Status(), page.URL())
	}
	visible(t, pageByRole(page, *playwright.AriaRoleHeading, "Plans and billing"))
	visible(t, page.GetByText("Demo billing"))

	proPlan := page.Locator("article").Filter(playwright.LocatorFilterOptions{
		Has: pageByRole(page, *playwright.AriaRoleHeading, "Pro", true),
	})
	click(t, byRole(proPlan, *playwright.AriaRoleButton, "Choose monthly"))
	must(t, page.WaitForURL(regexp.MustCompile(`/checkout/cs_local_`)))

	status := page.GetByRole(*playwright.AriaRoleStatus)
	visible(t, pageByRole(page, *playwright.AriaRoleHeading, "Local payment setup"))
	must(t, expect.Locator(status).ToContainText("Demo simulator only"))
	must(t, expect.Locator(status).ToContainText("No card details are collected"))
	confirm := pageByRole(page, *playwright.AriaRoleButton, "Confirm local demo setup")
	click(t, confirm)
	waitForPath(t, page, func(u *url.URL) bool {
		return u.Path == "/billing" && u.Query().Get("payment") == "success"
	})

	currentPlan := page.Locator(`section[aria-labelledby="billing-summary-title"]`)
	for _, text := range []string{"Pro", "Active", "On file"} {
		must(t, expect.Locator(currentPlan).ToContainText(text))
	}

	click(t, pageByRole(page, *playwright.AriaRoleLink, "Manage billing"))
	visible(t, pageByRole(page, *playwright.AriaRoleHeading, "Local billing portal"))
	must(t, expect.Locator(status).ToContainText("Demo simulator only"))
	must(t, expect.Locator(status).ToContainText("does not collect payment details or contact Stripe"))
}
