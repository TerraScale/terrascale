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

	proPlan := planCard(page, "Pro")
	for _, allowance := range []string{"25 GB-region storage", "100M operations/month", "25 databases", "25 team members", "30-day free trial"} {
		must(t, expect.Locator(proPlan).ToContainText(allowance))
	}
	click(t, byRole(proPlan, *playwright.AriaRoleButton, "Upgrade to Pro"))
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
	must(t, expect.Locator(byRole(proPlan, *playwright.AriaRoleButton, "Current plan")).ToBeDisabled())
	must(t, expect.Locator(page.Locator(".ts-billing-grid")).Not().ToContainText("free trial"))

	click(t, byRole(pageByRole(page, *playwright.AriaRoleNavigation, "Billing interval"), *playwright.AriaRoleLink, "Annual"))
	waitForPath(t, page, func(u *url.URL) bool { return u.Query().Get("interval") == "annual" })
	must(t, expect.Locator(proPlan).ToContainText("billed annually"))
	visible(t, byRole(proPlan, *playwright.AriaRoleButton, "Switch to annual billing"))
	click(t, byRole(proPlan, *playwright.AriaRoleButton, "Switch to annual billing"))
	waitForPath(t, page, func(u *url.URL) bool { return u.Path == "/usage" && u.Query().Get("review") == "1" })
	visible(t, pageByRole(page, *playwright.AriaRoleHeading, "Review plan change"))
	click(t, pageByRole(page, *playwright.AriaRoleLink, "Close plan review"))

	click(t, pageByRole(page, *playwright.AriaRoleLink, "Manage payment"))
	visible(t, pageByRole(page, *playwright.AriaRoleHeading, "Local billing portal"))
	must(t, expect.Locator(status).ToContainText("Demo simulator only"))
	must(t, expect.Locator(status).ToContainText("does not collect payment details or contact Stripe"))
}

func TestBillingPageShowsScheduledDowngradeUntilUndone(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	registerAndOnboard(t, page, newAccount("billing-scheduled"), "starter")
	review := page.Locator("#review-billing-plan")

	goToOK(t, page, "/billing")
	click(t, byRole(planCard(page, "Pro"), *playwright.AriaRoleButton, "Upgrade to Pro"))
	click(t, byRole(review, *playwright.AriaRoleButton, "Confirm change"))
	waitForPath(t, page, func(u *url.URL) bool { return u.Query().Get("billing") == "changed" })

	goToOK(t, page, "/billing")
	click(t, byRole(planCard(page, "Starter"), *playwright.AriaRoleButton, "Switch to Starter"))
	click(t, byRole(review, *playwright.AriaRoleButton, "Confirm change"))
	waitForPath(t, page, func(u *url.URL) bool { return u.Query().Get("billing") == "scheduled" })

	goToOK(t, page, "/billing")
	summary := page.Locator("#billing-summary")
	must(t, expect.Locator(summary).ToContainText("Change scheduled"))
	must(t, expect.Locator(summary).ToContainText("moves to Starter, billed monthly"))
	must(t, expect.Locator(planCard(page, "Starter")).ToContainText("Scheduled"))
	must(t, expect.Locator(byRole(planCard(page, "Business"), *playwright.AriaRoleButton, "Upgrade to Business")).ToBeDisabled())
	screenshot(t, page, "billing-scheduled-downgrade")

	click(t, byRole(summary, *playwright.AriaRoleButton, "Undo scheduled change"))
	waitForPath(t, page, func(u *url.URL) bool { return u.Query().Get("billing") == "undone" })
	goToOK(t, page, "/billing")
	must(t, expect.Locator(page.Locator("#billing-summary")).Not().ToContainText("Change scheduled"))
	must(t, expect.Locator(byRole(planCard(page, "Business"), *playwright.AriaRoleButton, "Upgrade to Business")).ToBeEnabled())
}

// planCard is the plan's card on the Plans and billing page.
func planCard(page playwright.Page, name string) playwright.Locator {
	return page.Locator("article").Filter(playwright.LocatorFilterOptions{
		Has: pageByRole(page, *playwright.AriaRoleHeading, name, true),
	})
}
