package e2e

import (
	"net/url"
	"testing"

	"github.com/mxschmitt/playwright-go"
)

// fillSignUp completes the sign-up form and submits it.
func fillSignUp(t *testing.T, page playwright.Page, account credentials) {
	t.Helper()
	form := page.Locator(`form[action="/onboarding"]`)
	fill(t, form.Locator(`[name="displayName"]`), account.DisplayName)
	fill(t, form.Locator(`[name="email"]`), account.Email)
	fill(t, form.Locator(`[name="password"]`), account.Password)
	fill(t, form.Locator(`[name="confirmPassword"]`), account.Password)
	must(t, form.Locator(`[name="terms"]`).Check())
	click(t, byRole(form, *playwright.AriaRoleButton, "Create account"))
}

func TestPricingPlanLinkPreselectsThePlanAfterSignUp(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	goTo(t, page, "/register?plan=pro")
	fillSignUp(t, page, newAccount("pricing-plan"))

	payment := onboardingStep(t, page, "payment-method")
	must(t, expect.Locator(payment.Locator(`[name="plan"][value="pro"]`)).ToBeChecked())
	must(t, expect.Locator(payment.Locator(`[name="plan"][value="starter"]`)).Not().ToBeChecked())
}

func TestSignUpFromSignInReturnsToTheRequestedPage(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	goTo(t, page, "/api-keys?kind=m2m")
	waitForPath(t, page, func(u *url.URL) bool { return u.Path == "/login" })
	click(t, pageByRole(page, *playwright.AriaRoleLink, "Create account"))
	waitForPath(t, page, func(u *url.URL) bool { return u.Path == "/onboarding" })

	fillSignUp(t, page, newAccount("sign-up-return"))
	waitForPath(t, page, func(u *url.URL) bool { return u.Path == "/api-keys" && u.Query().Get("kind") == "m2m" })
	visible(t, pageByRole(page, *playwright.AriaRoleHeading, "API keys").First())
}
