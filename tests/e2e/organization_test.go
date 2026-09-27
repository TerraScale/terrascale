package e2e

import (
	"testing"

	"github.com/mxschmitt/playwright-go"
)

// TestOwnershipTransferIsConfirmedAndCanBeCanceled requests a transfer in the
// sample organization and cancels it again. It stays serial so no parallel
// test sees the sample organization mid-transfer.
func TestOwnershipTransferIsConfirmedAndCanBeCanceled(t *testing.T) {
	const labs, ravi = "org_01JC9Q5V2N4P6T8R0X2Z4B6D8", "usr_01JC9S9V6Q1X4Z6B8D0F3H6JN"
	page := newPage(t)
	login(t, page, sampleAccount.Email, sampleAccount.Password)
	goToOK(t, page, "/organization")
	switchForm := page.Locator("#organization-switch form")
	selectOption(t, switchForm.Locator(`[name="organizationId"]`), labs)
	submitAndExpect(t, page, "/api/v1/dashboard/switch-organization", 303, byRole(switchForm, *playwright.AriaRoleButton, "Switch organization").Click)

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
