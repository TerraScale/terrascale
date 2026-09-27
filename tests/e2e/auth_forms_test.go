package e2e

import (
	"net/url"
	"testing"

	"github.com/mxschmitt/playwright-go"
)

// Rejected sign-up and sign-in forms answer 4xx, which Chromium logs as a
// console error, so this journey uses an unwatched page.
func TestSignUpAndSignInKeepInputAndPointAtTheFieldToFix(t *testing.T) {
	t.Parallel()
	account := newAccount("form-errors")
	page, err := newContext(t, baseURL).NewPage()
	must(t, err)

	goTo(t, page, "/onboarding")
	form := page.Locator(`form[action="/onboarding"]`)
	visible(t, form.GetByText("Use at least 12 characters."))
	fill(t, form.Locator(`[name="displayName"]`), account.DisplayName)
	fill(t, form.Locator(`[name="email"]`), account.Email)
	fill(t, form.Locator(`[name="password"]`), account.Password)
	fill(t, form.Locator(`[name="confirmPassword"]`), account.Password+"x")
	must(t, form.Locator(`[name="terms"]`).Check())
	click(t, byRole(form, *playwright.AriaRoleButton, "Create account"))

	confirmation := form.Locator("#confirmPassword")
	must(t, expect.Locator(confirmation).ToBeFocused())
	must(t, expect.Locator(confirmation).ToHaveAttribute("aria-invalid", "true"))
	must(t, expect.Locator(confirmation).ToHaveAccessibleDescription("Passwords do not match."))
	must(t, expect.Locator(form.Locator("#displayName")).ToHaveValue(account.DisplayName))
	must(t, expect.Locator(form.Locator("#email")).ToHaveValue(account.Email))
	must(t, expect.Locator(form.Locator("#terms")).ToBeChecked())
	must(t, expect.Locator(form.Locator("#password")).ToHaveValue(""))
	if path := currentPath(t, page); path != "/onboarding" {
		t.Fatalf("rejected sign-up is at %s, want /onboarding", path)
	}
	screenshot(t, page, "sign-up-error")

	fill(t, form.Locator(`[name="password"]`), account.Password)
	fill(t, confirmation, account.Password)
	click(t, byRole(form, *playwright.AriaRoleButton, "Create account"))
	onboardingStep(t, page, "payment-method")
	logout(t, page)

	goTo(t, page, "/onboarding")
	fill(t, form.Locator(`[name="displayName"]`), account.DisplayName)
	fill(t, form.Locator(`[name="email"]`), account.Email)
	fill(t, form.Locator(`[name="password"]`), account.Password)
	fill(t, form.Locator(`[name="confirmPassword"]`), account.Password)
	must(t, form.Locator(`[name="terms"]`).Check())
	click(t, byRole(form, *playwright.AriaRoleButton, "Create account"))
	email := form.Locator("#email")
	must(t, expect.Locator(email).ToBeFocused())
	must(t, expect.Locator(email).ToHaveAccessibleDescription("An account with this email already exists. Sign in to it, or use another email."))
	click(t, byRole(form, *playwright.AriaRoleLink, "Sign in"))

	signIn := page.Locator(`form[action="/login"]`)
	click(t, byRole(signIn, *playwright.AriaRoleButton, "Sign in"))
	must(t, expect.Locator(signIn.Locator("#email")).ToBeFocused())
	must(t, expect.Locator(signIn.Locator("#email")).ToHaveAccessibleDescription("Enter your email address."))
	visible(t, signIn.GetByText("Enter your password."))

	fill(t, signIn.Locator(`[name="email"]`), account.Email)
	fill(t, signIn.Locator(`[name="password"]`), "not-the-password")
	click(t, byRole(signIn, *playwright.AriaRoleButton, "Sign in"))
	visible(t, page.GetByRole(*playwright.AriaRoleAlert).GetByText("Incorrect email or password."))
	must(t, expect.Locator(signIn.Locator("#email")).ToHaveValue(account.Email))
	must(t, expect.Locator(signIn.Locator("#password")).ToBeFocused())
	screenshot(t, page, "sign-in-error")

	fill(t, signIn.Locator(`[name="password"]`), account.Password)
	click(t, byRole(signIn, *playwright.AriaRoleButton, "Sign in"))
	waitForPath(t, page, func(u *url.URL) bool { return u.Path == "/onboarding" })
	onboardingStep(t, page, "payment-method")
}
