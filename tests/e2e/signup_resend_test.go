package e2e

import (
	"encoding/json/v2"
	"net/http"
	"net/http/httptest"
	"testing"
	"time"

	"github.com/mxschmitt/playwright-go"
	"terrascale.tech/dashboard/internal/app"
	"terrascale.tech/dashboard/internal/platform/config"
)

func TestSignUpResendCountdown(t *testing.T) {
	mux := http.NewServeMux()
	issuer := httptest.NewServer(mux)
	t.Cleanup(issuer.Close)
	mux.HandleFunc("GET /.well-known/openid-configuration", func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		_ = json.MarshalWrite(w, map[string]any{
			"issuer": issuer.URL, "authorization_endpoint": issuer.URL + "/authorize", "token_endpoint": issuer.URL + "/token",
			"jwks_uri": issuer.URL + "/jwks", "response_types_supported": []string{"code"}, "subject_types_supported": []string{"public"},
			"id_token_signing_alg_values_supported": []string{"ES256"}, "code_challenge_methods_supported": []string{"S256"},
			"grant_types_supported": []string{"urn:terrascale:params:oauth:grant-type:password", "urn:terrascale:params:oauth:grant-type:email-code"},
		})
	})
	mux.HandleFunc("POST /headless/v1/sign-up", func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		_ = json.MarshalWrite(w, map[string]string{"pending_token": "browser-test-pending-token"})
	})
	application, err := app.New(config.Config{
		Demo: true, TSAuth: config.TSAuth{
			Issuer: issuer.URL, ClientID: "test-dashboard", ClientSecret: "browser-test-client-secret",
			RedirectURL: "http://dashboard.test/login/callback",
		},
	})
	must(t, err)
	dashboard := httptest.NewServer(application.Handler)
	t.Cleanup(dashboard.Close)
	page := watchedPageExcept(t, newContext(t, dashboard.URL), func(message playwright.ConsoleMessage) bool {
		return rejectedFormDocument.MatchString(message.Text())
	})
	now := time.Now().UnixMilli()
	must(t, page.Clock().Install(playwright.ClockInstallOptions{Time: now - 3_600_000}))
	must(t, page.Clock().PauseAt(now))
	goTo(t, page, "/onboarding")
	account := newAccount("resend-code")
	form := page.Locator(`form[action="/onboarding"]`)
	fill(t, form.Locator(`[name="displayName"]`), account.DisplayName)
	fill(t, form.Locator(`[name="email"]`), account.Email)
	fill(t, form.Locator(`[name="password"]`), account.Password)
	fill(t, form.Locator(`[name="confirmPassword"]`), account.Password)
	must(t, form.Locator(`[name="terms"]`).Check())
	click(t, byRole(form, *playwright.AriaRoleButton, "Create account"))
	button := page.Locator(`form[action="/login/code/resend"] button`)
	must(t, expect.Locator(button).ToHaveText("Resend code (60s)"))
	must(t, expect.Locator(button).ToBeDisabled())
	must(t, expect.Locator(button).ToHaveClass("ts-btn ts-btn-outline ts-btn-lg ts-btn-block"))
	must(t, expect.Locator(page.GetByText("No email?")).ToHaveCount(0))
	must(t, page.Clock().FastForward(59_000))
	must(t, expect.Locator(button).ToHaveText("Resend code (1s)"))
	must(t, expect.Locator(button).ToBeDisabled())
	must(t, page.Clock().FastForward(1_000))
	must(t, expect.Locator(button).ToHaveText("Resend code"))
	must(t, expect.Locator(button).ToBeEnabled())
	screenshot(t, page, "ts631-resend-ready")
	// Moving the browser's clock cannot bypass the server's real minute.
	click(t, button)
	must(t, expect.Locator(button).ToBeDisabled())
	visible(t, page.GetByRole(*playwright.AriaRoleAlert).GetByText("before resending the code.", playwright.LocatorGetByTextOptions{Exact: new(false)}))
	screenshot(t, page, "ts631-resend-cooldown")
}
