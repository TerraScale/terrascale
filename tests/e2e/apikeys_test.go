package e2e

import (
	"net/url"
	"testing"

	"github.com/mxschmitt/playwright-go"
)

// TestAPIKeySecretsAreShownOnceAndChangesAreConfirmed covers the credential
// lifecycle: invalid input, a secret that a reload cannot reissue, and
// confirmed rotation and revocation.
func TestAPIKeySecretsAreShownOnceAndChangesAreConfirmed(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	registerAndOnboard(t, page, newAccount("api-keys"))
	keyName := uniqueName("deploy")
	goToOK(t, page, "/api-keys?create=key")
	dialog := page.Locator("dialog#create-key")
	create := dialog.Locator(`form[action="/api/v1/dashboard/credentials/key"]`)
	fill(t, create.Locator(`[name="name"]`), keyName)
	fill(t, create.Locator(`[name="scopes"]`), ",,,")
	submitAndExpect(t, page, "/api/v1/dashboard/credentials/key", 303, byRole(create, *playwright.AriaRoleButton, "Create key").Click)
	waitForPath(t, page, func(u *url.URL) bool { return u.Path == "/api-keys" && u.Query().Get("create") == "key" })
	visible(t, dialog)
	must(t, expect.Locator(dialog.GetByRole(*playwright.AriaRoleAlert)).ToContainText("at least one scope"))

	fill(t, create.Locator(`[name="name"]`), keyName)
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

	click(t, byRole(row, *playwright.AriaRoleButton, "Rotate"))
	confirm := page.Locator("dialog[open]")
	visible(t, byRole(confirm, *playwright.AriaRoleHeading, "Rotate "+keyName+"?"))
	submitAndExpect(t, page, "/api/v1/dashboard/credentials/rotate", 200, byRole(confirm, *playwright.AriaRoleButton, "Rotate secret").Click)
	must(t, expect.Locator(secret.GetByRole(*playwright.AriaRoleHeading)).ToHaveText("New secret for "+keyName))
	must(t, expect.Locator(secret.Locator("code")).Not().ToHaveText(first))
	waitForPath(t, page, func(u *url.URL) bool { return u.Path == "/api-keys" })

	click(t, byRole(row, *playwright.AriaRoleButton, "Revoke"))
	visible(t, byRole(confirm, *playwright.AriaRoleHeading, "Revoke "+keyName+"?"))
	submitAndExpect(t, page, "/api/v1/dashboard/credentials/revoke", 303, byRole(confirm, *playwright.AriaRoleButton, "Revoke credential").Click)
	must(t, expect.Locator(row).ToContainText("Revoked"))
	must(t, expect.Locator(byRole(row, *playwright.AriaRoleButton, "Rotate")).ToHaveCount(0))
}
