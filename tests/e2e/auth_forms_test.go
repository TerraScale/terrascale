package e2e

import (
	"fmt"
	"net/url"
	"testing"

	"github.com/mxschmitt/playwright-go"
)

func TestLoginPanelLayout(t *testing.T) {
	page := newPage(t)
	for _, size := range [][2]int{{1914, 901}, {1280, 800}, {390, 844}, {320, 640}} {
		t.Run(fmt.Sprintf("%dx%d", size[0], size[1]), func(t *testing.T) {
			must(t, page.SetViewportSize(size[0], size[1]))
			goTo(t, page, "/login")
			viewportScreenshot(t, page, fmt.Sprintf("ts291-login-%dx%d", size[0], size[1]))
			_, err := page.Evaluate(`async () => {
				await document.fonts.ready;
				const box = selector => document.querySelector(selector).getBoundingClientRect();
				const near = (a, b) => Math.abs(a - b) < 1;
				const email = box('#email');
				for (const selector of ['#password', '.ts-form > button', '.ts-auth-providers']) {
					const control = box(selector);
					if (!near(control.left, email.left) || !near(control.right, email.right))
						throw new Error(selector + ' is not aligned with email');
				}
				const providers = [...document.querySelectorAll('.ts-auth-providers button')];
				const first = providers[0].getBoundingClientRect();
				for (const provider of providers) {
					const rect = provider.getBoundingClientRect();
					if (!near(rect.top, first.top) || !near(rect.width, first.width) || !near(rect.height, first.height))
						throw new Error('Providers must have equal sizes in one row');
					if (provider.scrollWidth > provider.clientWidth)
						throw new Error('Provider label overflows');
				}
				const account = box('.ts-login-panel > .ts-auth-footer');
				const locale = box('.ts-login-footer');
				if (!near(account.bottom, locale.top) || account.top < box('.ts-auth-providers').bottom + 19)
					throw new Error('Account link must sit above the locale footer, separated from providers');
				if (innerWidth === 1914 && (!near(email.width, 384) || !near(box('.ts-login-heading').top, 16) || !near(box('.ts-form').top, 324)))
					throw new Error('Desktop panel does not follow the reference guides');
				if (document.documentElement.scrollWidth > innerWidth)
					throw new Error('Login page overflows horizontally');
			}`)
			must(t, err)
		})
	}
}

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
