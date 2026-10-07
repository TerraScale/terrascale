package e2e

import (
	"regexp"
	"testing"

	"github.com/mxschmitt/playwright-go"
)

func TestOnboardingBillingIntervalUpdatesPricesAndCheckout(t *testing.T) {
	for _, javascript := range []bool{true, false} {
		name := "with-javascript"
		if !javascript {
			name = "without-javascript"
		}
		t.Run(name, func(t *testing.T) {
			context, err := browser.NewContext(playwright.BrowserNewContextOptions{
				BaseURL: new(baseURL), JavaScriptEnabled: new(javascript),
			})
			must(t, err)
			t.Cleanup(func() { _ = context.Close() })
			page := watchedPage(t, context)
			registerAndOpenPaymentStep(t, page, newAccount("billing-interval"))
			payment := onboardingStep(t, page, "payment-method")
			plans := payment.Locator(".ts-onboarding-plan")
			must(t, expect.Locator(plans).ToHaveCount(3))
			starterPrice := plans.First().Locator(".ts-onboarding-plan-price:visible")
			monthly := payment.Locator(`[name="interval"][value="monthly"]`)
			annual := payment.Locator(`[name="interval"][value="annual"]`)
			pro := payment.Locator(`[name="plan"][value="pro"]`)
			must(t, payment.Locator(`label:has(input[name="plan"][value="pro"])`).Click())

			monthlyPrice := regexp.MustCompile(`^\$5\.00\s*/ month$`)
			annualPrice := regexp.MustCompile(`^\$50\.00\s*/ year$`)
			assertInterval := func(active, inactive playwright.Locator, price *regexp.Regexp) {
				t.Helper()
				must(t, expect.Locator(active).ToBeChecked())
				must(t, expect.Locator(inactive).Not().ToBeChecked())
				must(t, expect.Locator(payment.Locator(".ts-onboarding-plan-price:visible")).ToHaveCount(3))
				must(t, expect.Locator(starterPrice).ToHaveText(price))
				must(t, expect.Locator(pro).ToBeChecked())
			}
			assertInterval(monthly, annual, monthlyPrice)
			screenshot(t, page, "onboarding-monthly")
			must(t, payment.Locator(`label:has(input[name="interval"][value="annual"])`).Click())
			screenshot(t, page, "onboarding-annual")
			assertInterval(annual, monthly, annualPrice)
			must(t, payment.Locator(`label:has(input[name="interval"][value="monthly"])`).Click())
			assertInterval(monthly, annual, monthlyPrice)

			// The native radio group also switches with the keyboard.
			must(t, monthly.Focus())
			must(t, monthly.Press("ArrowRight"))
			assertInterval(annual, monthly, annualPrice)
			billed := "Pro · billed annually"
			if !javascript {
				must(t, annual.Press("ArrowLeft"))
				assertInterval(monthly, annual, monthlyPrice)
				billed = "Pro · billed monthly"
			}
			click(t, byRole(payment, *playwright.AriaRoleButton, "Continue to checkout"))
			must(t, page.WaitForURL(regexp.MustCompile(`/checkout/cs_local_`)))
			must(t, expect.Locator(page.Locator(".ts-auth-subtitle")).ToContainText(billed))
		})
	}
}
