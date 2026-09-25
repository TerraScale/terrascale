package e2e

import (
	"net/url"
	"testing"

	"github.com/mxschmitt/playwright-go"
)

// registerAndOnboard creates a fresh demo account and finishes the payment
// and project onboarding steps.
func registerAndOnboard(t *testing.T, page playwright.Page, account credentials) {
	t.Helper()
	registerAndOpenPaymentStep(t, page, account)

	payment := onboardingStep(t, page, "payment-method")
	click(t, byRole(payment, *playwright.AriaRoleButton, "Continue on Free"))

	project := onboardingStep(t, page, "project")
	fill(t, project.Locator(`[name="name"]`), "Personal")
	selectOption(t, project.Locator(`[name="region"]`), "GRU")
	click(t, byRole(project, *playwright.AriaRoleButton, "Create project"))
	waitForPath(t, page, func(u *url.URL) bool { return u.Path == "/" && u.RawQuery == "" })
}

// registerAndOpenPaymentStep registers an account and leaves it on the
// payment-method step.
func registerAndOpenPaymentStep(t *testing.T, page playwright.Page, account credentials) {
	t.Helper()
	goTo(t, page, "/onboarding")
	form := page.Locator(`form[action="/onboarding/register"]`)
	fill(t, form.Locator(`[name="displayName"]`), account.DisplayName)
	fill(t, form.Locator(`[name="email"]`), account.Email)
	fill(t, form.Locator(`[name="password"]`), account.Password)
	fill(t, form.Locator(`[name="confirmPassword"]`), account.Password)
	must(t, form.Locator(`[name="terms"]`).Check())
	click(t, byRole(form, *playwright.AriaRoleButton, "Save"))

	waitForPath(t, page, func(u *url.URL) bool { return u.Path == "/onboarding" })
	onboardingStep(t, page, "payment-method")
}

func login(t *testing.T, page playwright.Page, email, password string) {
	t.Helper()
	goTo(t, page, "/login")
	form := page.Locator(`form[action="/api/v1/auth/password/login"]`)
	fill(t, form.Locator(`[name="email"]`), email)
	fill(t, form.Locator(`[name="password"]`), password)
	click(t, byRole(form, *playwright.AriaRoleButton, "Sign in"))
	waitForPath(t, page, func(u *url.URL) bool { return u.Path != "/login" })
}

func logout(t *testing.T, page playwright.Page) {
	t.Helper()
	goTo(t, page, "/")
	_, err := page.Locator(`form[action="/api/v1/auth/logout"]`).Evaluate(`form => form.requestSubmit()`, nil)
	must(t, err)
	waitForPath(t, page, func(u *url.URL) bool { return u.Path == "/login" })
}

func onboardingStep(t *testing.T, page playwright.Page, name string) playwright.Locator {
	t.Helper()
	form := page.Locator(`form[action="/onboarding/step"]`).Filter(playwright.LocatorFilterOptions{
		Has: page.Locator(`[name="step"][value="` + name + `"]`),
	})
	must(t, form.WaitFor(playwright.LocatorWaitForOptions{State: playwright.WaitForSelectorStateVisible}))
	return form
}
