package e2e

import (
	"net/url"
	"strings"
	"testing"

	"github.com/mxschmitt/playwright-go"
)

func TestPersonalOwnerChangesPlanAndUndoesScheduledChange(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	registerAndOnboard(t, page, newAccount("plan-owner"), "starter")
	goToOK(t, page, "/usage?tab=billing")
	visible(t, pageByRole(page, *playwright.AriaRoleButton, "Change plan"))
	visible(t, pageByRole(page, *playwright.AriaRoleLink, "Manage payment"))
	screenshot(t, page, "stripe-billing-before")

	picker := page.Locator("#change-billing-plan")
	review := page.Locator("#review-billing-plan")
	click(t, pageByRole(page, *playwright.AriaRoleButton, "Change plan"))
	visible(t, picker)
	must(t, picker.Locator(`[name="plan"][value="pro"]`).Check())
	must(t, picker.Locator(`[name="interval"][value="annual"]`).Check())
	click(t, byRole(picker, *playwright.AriaRoleButton, "Review change"))
	visible(t, review)
	must(t, expect.Locator(review).ToContainText("US$ 500.00"))
	must(t, expect.Locator(review).ToContainText("Demo only; no Stripe charge"))
	screenshot(t, page, "stripe-review-upgrade")
	click(t, byRole(review, *playwright.AriaRoleButton, "Confirm change"))
	waitForPath(t, page, func(u *url.URL) bool { return u.Path == "/usage" && u.Query().Get("billing") == "changed" })
	summary := page.Locator("#billing-summary")
	must(t, expect.Locator(summary).ToContainText("Pro"))

	click(t, pageByRole(page, *playwright.AriaRoleButton, "Change plan"))
	must(t, picker.Locator(`[name="plan"][value="starter"]`).Check())
	click(t, byRole(picker, *playwright.AriaRoleButton, "Review change"))
	must(t, expect.Locator(review).ToContainText("At renewal"))
	click(t, byRole(review, *playwright.AriaRoleButton, "Confirm change"))
	must(t, expect.Locator(summary).ToContainText("Change scheduled"))
	must(t, expect.Locator(summary).ToContainText("Pro"))
	screenshot(t, page, "stripe-billing-scheduled")
	click(t, pageByRole(page, *playwright.AriaRoleButton, "Undo scheduled change"))
	text, err := summary.TextContent()
	must(t, err)
	if strings.Contains(text, "Change scheduled") {
		t.Fatal("scheduled change remained after undo")
	}

	click(t, pageByRole(page, *playwright.AriaRoleButton, "Change plan"))
	must(t, picker.Locator(`[name="plan"][value="free"]`).Check())
	click(t, byRole(picker, *playwright.AriaRoleButton, "Review change"))
	must(t, expect.Locator(review).ToContainText("No charge today"))
	click(t, byRole(review, *playwright.AriaRoleButton, "Confirm change"))
	must(t, expect.Locator(summary).ToContainText("free"))
}
