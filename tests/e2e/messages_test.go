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
	securityTab := "/profile?tab=security"
	notShown := func() {
		t.Helper()
		visible(t, pageByRole(page, *playwright.AriaRoleHeading, "Change password"))
		must(t, expect.Locator(page.GetByText(planted)).ToHaveCount(0))
	}

	goTo(t, page, securityTab+"&error="+url.QueryEscape(planted))
	notShown()

	// A failed form post redirects with its message, which a reload keeps.
	password := page.Locator(`form[action="/api/v1/dashboard/profile/password"]`)
	fill(t, password.Locator(`[name="currentPassword"]`), "not-the-password")
	fill(t, password.Locator(`[name="newPassword"]`), "E2e-messages-next-123!")
	fill(t, password.Locator(`[name="confirmPassword"]`), "E2e-messages-next-123!")
	click(t, byRole(password, *playwright.AriaRoleButton, "Change password and sign out"))
	waitForPath(t, page, func(u *url.URL) bool { return u.Query().Get("error") != "" })
	alert := page.GetByRole(*playwright.AriaRoleAlert)
	must(t, expect.Locator(alert).ToContainText("Current password is incorrect."))
	_, err := page.Reload()
	must(t, err)
	must(t, expect.Locator(alert).ToContainText("Current password is incorrect."))

	// Signing in cannot deliver a message planted in the return path.
	logout(t, page)
	goTo(t, page, "/login?returnTo="+url.QueryEscape(securityTab+"&error="+url.QueryEscape(planted)))
	form := page.Locator(`form[action="/login"]`)
	fill(t, form.Locator(`[name="email"]`), account.Email)
	fill(t, form.Locator(`[name="password"]`), account.Password)
	click(t, byRole(form, *playwright.AriaRoleButton, "Sign in"))
	waitForPath(t, page, func(u *url.URL) bool { return u.Path == "/profile" })
	notShown()

	goTo(t, page, "/organization?error="+url.QueryEscape(planted))
	visible(t, pageByRole(page, *playwright.AriaRoleHeading, "Organization details"))
	must(t, expect.Locator(page.GetByText(planted)).ToHaveCount(0))
}
