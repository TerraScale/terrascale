package e2e

import (
	"net/url"
	"testing"

	"github.com/mxschmitt/playwright-go"
)

// TestAPIKeySecretsAreShownOnceAndChangesAreConfirmed covers the credential
// lifecycle: invalid input that keeps what was typed, a secret that a reload
// cannot reissue, and confirmed rotation and revocation that keep the list's
// filters.
func TestAPIKeySecretsAreShownOnceAndChangesAreConfirmed(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	registerAndOnboard(t, page, newAccount("api-keys"))
	keyName := uniqueName("deploy")
	goToOK(t, page, "/api-keys")
	must(t, expect.Locator(page.Locator(".ts-page-actions button")).ToHaveCount(0))
	must(t, expect.Locator(page.Locator("#credentials .ts-card-actions button")).ToHaveCount(1))
	must(t, expect.Locator(byRole(page.Locator(".ts-page"), *playwright.AriaRoleButton, "Create machine credential")).ToHaveCount(0))
	goToOK(t, page, "/api-keys?kind=m2m")
	click(t, byRole(page.Locator("#credentials .ts-card-actions"), *playwright.AriaRoleButton, "Create machine credential"))
	visible(t, page.Locator("dialog#create-m2m"))
	goToOK(t, page, "/api-keys")
	click(t, byRole(page.Locator("#credentials .ts-card-actions"), *playwright.AriaRoleButton, "Create key"))
	dialog := page.Locator("dialog#create-key")
	visible(t, dialog)
	create := dialog.Locator(`form[action="/api/v1/dashboard/credentials/key"]`)
	fill(t, create.Locator(`[name="name"]`), keyName)
	fill(t, create.Locator(`[name="scopes"]`), ",,,")
	submitAndExpect(t, page, "/api/v1/dashboard/credentials/key", 303, byRole(create, *playwright.AriaRoleButton, "Create key").Click)
	waitForPath(t, page, func(u *url.URL) bool { return u.Path == "/api-keys" && u.Query().Get("create") == "key" })
	visible(t, dialog)
	must(t, expect.Locator(dialog.GetByRole(*playwright.AriaRoleAlert)).ToContainText("1 to 16 scopes"))
	must(t, expect.Locator(create.Locator(`[name="name"]`)).ToHaveValue(keyName))
	must(t, expect.Locator(create.Locator(`[name="scopes"]`)).ToHaveValue(",,,"))

	fill(t, create.Locator(`[name="scopes"]`), "database:read")
	submitAndExpect(t, page, "/api/v1/dashboard/credentials/key", 200, byRole(create, *playwright.AriaRoleButton, "Create key").Click)
	secret := page.Locator(".ts-apikeys-secret")
	must(t, expect.Locator(secret.GetByRole(*playwright.AriaRoleHeading)).ToHaveText("Secret for "+keyName))
	waitForPath(t, page, func(u *url.URL) bool { return u.Path == "/api-keys" && u.RawQuery == "" })
	first, err := secret.Locator("code").TextContent()
	must(t, err)

	_, err = page.Reload()
	must(t, err)
	row := page.Locator("#credentials tbody tr").Filter(playwright.LocatorFilterOptions{HasText: keyName})
	must(t, expect.Locator(row).ToHaveCount(1))
	must(t, expect.Locator(secret).ToHaveCount(0))

	// Rotating and revoking from a filtered list keep its filters.
	goToOK(t, page, "/api-keys?kind=key")
	must(t, expect.Locator(row).ToHaveCount(1))

	click(t, byRole(row, *playwright.AriaRoleButton, "Rotate"))
	confirm := page.Locator("dialog[open]")
	visible(t, byRole(confirm, *playwright.AriaRoleHeading, "Rotate "+keyName+"?"))
	submitAndExpect(t, page, "/api/v1/dashboard/credentials/rotate", 200, byRole(confirm, *playwright.AriaRoleButton, "Rotate secret").Click)
	must(t, expect.Locator(secret.GetByRole(*playwright.AriaRoleHeading)).ToHaveText("New secret for "+keyName))
	must(t, expect.Locator(secret.Locator("code")).Not().ToHaveText(first))
	waitForPath(t, page, func(u *url.URL) bool { return u.Path == "/api-keys" && u.RawQuery == "kind=key" })

	click(t, byRole(row, *playwright.AriaRoleButton, "Revoke"))
	visible(t, byRole(confirm, *playwright.AriaRoleHeading, "Revoke "+keyName+"?"))
	submitAndExpect(t, page, "/api/v1/dashboard/credentials/revoke", 303, byRole(confirm, *playwright.AriaRoleButton, "Revoke credential").Click)
	waitForPath(t, page, func(u *url.URL) bool { return u.Path == "/api-keys" && u.RawQuery == "kind=key" })
	must(t, expect.Locator(row).ToContainText("Revoked"))
	must(t, expect.Locator(byRole(row, *playwright.AriaRoleButton, "Rotate")).ToHaveCount(0))
}
