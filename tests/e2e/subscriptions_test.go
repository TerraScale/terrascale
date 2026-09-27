package e2e

import (
	"strings"
	"testing"

	"github.com/mxschmitt/playwright-go"
)

func TestSubscriptionsExplainRefusalsResumeAndConfirmDetach(t *testing.T) {
	t.Parallel()
	page := formErrorPage(t)
	login(t, page, sampleAccount.Email, sampleAccount.Password)
	goToOK(t, page, "/subscriptions")

	form := page.Locator(`form[action="/api/v1/dashboard/subscriptions/attach"]`)
	save := func(name, query, transport string, status int) {
		t.Helper()
		codeEditor(t, page, "subscription-query")
		fill(t, form.Locator("#subscription-name"), name)
		fill(t, form.Locator("#subscription-query"), query)
		selectOption(t, form.Locator("#subscription-transport"), transport)
		submitAndExpect(t, page, "/api/v1/dashboard/subscriptions/attach", status, byRole(form, *playwright.AriaRoleButton, "Save configuration").Click)
	}

	// A query the demo cannot share is explained beside the form, which keeps
	// what was typed.
	name := strings.ReplaceAll(uniqueName("e2e_view"), "-", "_")
	save(name, "SELECT id FROM orders", "websocket", 400)
	must(t, expect.Locator(form.GetByRole(*playwright.AriaRoleAlert)).ToContainText("SELECT * FROM collection"))
	must(t, expect.Locator(form.Locator("#subscription-name")).ToHaveValue(name))
	must(t, expect.Locator(form.Locator("#subscription-query")).ToHaveValue("SELECT id FROM orders"))
	must(t, expect.Locator(form.Locator("#subscription-transport")).ToHaveValue("websocket"))

	save(name, "SELECT * FROM orders LIMIT 10", "websocket", 303)
	row := page.Locator("#saved-configurations tbody tr").Filter(playwright.LocatorFilterOptions{HasText: name})
	must(t, expect.Locator(row).ToContainText("active"))
	must(t, expect.Locator(row).ToContainText("WebSocket"))

	save(name, "SELECT * FROM orders LIMIT 10", "http", 409)
	must(t, expect.Locator(form.GetByRole(*playwright.AriaRoleAlert)).ToContainText("Another configuration already uses that view name."))

	// Pausing is no longer one-way.
	submitAndExpect(t, page, "/api/v1/dashboard/subscriptions/pause", 303, byRole(row, *playwright.AriaRoleButton, "Pause "+name).Click)
	must(t, expect.Locator(row).ToContainText("paused"))
	submitAndExpect(t, page, "/api/v1/dashboard/subscriptions/resume", 303, byRole(row, *playwright.AriaRoleButton, "Resume "+name).Click)
	must(t, expect.Locator(row).ToContainText("active"))

	// Detaching asks first.
	click(t, byRole(row, *playwright.AriaRoleButton, "Detach "+name))
	confirm := pageByRole(page, *playwright.AriaRoleDialog, "Detach "+name+"?")
	visible(t, confirm)
	click(t, byRole(confirm, *playwright.AriaRoleButton, "Cancel"))
	must(t, expect.Locator(confirm).ToBeHidden())
	must(t, expect.Locator(row).ToHaveCount(1))
	click(t, byRole(row, *playwright.AriaRoleButton, "Detach "+name))
	submitAndExpect(t, page, "/api/v1/dashboard/subscriptions/detach", 303, byRole(confirm, *playwright.AriaRoleButton, "Detach view").Click)
	must(t, expect.Locator(row).ToHaveCount(0))
}
