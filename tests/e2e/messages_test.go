package e2e

import (
	"net/url"
	"testing"

	"github.com/mxschmitt/playwright-go"
)

// TestStatusMessagesComeOnlyFromDashboardRedirects plants a message in page
// links the way a phishing email would, and checks the browser shows only
// the messages the dashboard's own redirects carry.
func TestStatusMessagesComeOnlyFromDashboardRedirects(t *testing.T) {
	t.Parallel()
	const planted = "Your account is suspended. Call +1 555 0100."
	page := newPage(t)
	account := newAccount("messages")
	registerAndOnboard(t, page, account)
	logout(t, page)
	signIn := func(password string) {
		t.Helper()
		form := page.Locator(`form[action="/api/v1/auth/password/login"]`)
		fill(t, form.Locator(`[name="email"]`), account.Email)
		fill(t, form.Locator(`[name="password"]`), password)
		click(t, byRole(form, *playwright.AriaRoleButton, "Sign in"))
	}
	notShown := func() {
		t.Helper()
		must(t, expect.Locator(page.GetByText(planted)).ToHaveCount(0))
	}

	goTo(t, page, "/login?error="+url.QueryEscape(planted))
	visible(t, pageByRole(page, *playwright.AriaRoleHeading, "Sign in"))
	notShown()

	// The sign-in page's own redirect still carries its message, and a
	// reload keeps it.
	signIn("not-the-password")
	must(t, expect.Locator(page.GetByRole(*playwright.AriaRoleAlert)).ToContainText("Incorrect email or password."))
	_, err := page.Reload()
	must(t, err)
	must(t, expect.Locator(page.GetByRole(*playwright.AriaRoleAlert)).ToContainText("Incorrect email or password."))

	// Signing in cannot deliver a message planted in the return path.
	goTo(t, page, "/login?returnTo="+url.QueryEscape("/organization?error="+url.QueryEscape(planted)))
	signIn(account.Password)
	waitForPath(t, page, func(u *url.URL) bool { return u.Path == "/organization" })
	visible(t, pageByRole(page, *playwright.AriaRoleHeading, "Organization details"))
	notShown()

	goTo(t, page, "/organization?error="+url.QueryEscape(planted))
	visible(t, pageByRole(page, *playwright.AriaRoleHeading, "Organization details"))
	notShown()
}
