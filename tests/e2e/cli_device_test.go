package e2e

import (
	"net/url"
	"strings"
	"testing"
	"time"

	"github.com/mxschmitt/playwright-go"
)

func TestCLIDeviceCodeSurvivesSignInAndBrowserDecision(t *testing.T) {
	for _, decision := range []string{"approve", "deny"} {
		t.Run(decision, func(t *testing.T) {
			page := newPage(t)
			request := page.Request()
			response, err := request.Post("/api/v1/dashboard/cli/device-authorization", playwright.APIRequestContextPostOptions{
				Form: map[string]any{"client_id": "terrascale-cli"},
			})
			must(t, err)
			var challenge struct {
				Device   string `json:"device_code"`
				Code     string `json:"user_code"`
				Complete string `json:"verification_uri_complete"`
				Interval int    `json:"interval"`
			}
			responseJSON(t, response, 200, &challenge)
			if challenge.Device == "" || challenge.Code == "" || challenge.Interval != 5 {
				t.Fatal("invalid device challenge")
			}
			goTo(t, page, challenge.Complete)
			click(t, pageByRole(page, *playwright.AriaRoleLink, "Sign in to approve"))
			form := page.Locator(`form[action="/login"]`)
			fill(t, form.Locator(`[name="email"]`), admin.Email)
			fill(t, form.Locator(`[name="password"]`), admin.Password)
			click(t, byRole(form, *playwright.AriaRoleButton, "Sign in"))
			waitForPath(t, page, func(u *url.URL) bool {
				return u.Path == "/cli/approve" && u.Query().Get("user_code") == challenge.Code
			})
			visible(t, page.GetByText(challenge.Code, playwright.PageGetByTextOptions{Exact: new(true)}))
			form = page.Locator(`form[action="/cli/approve/submit"]`)
			if decision == "approve" {
				fill(t, form.Locator(`[name="password"]`), "wrong-password")
				click(t, byRole(form, *playwright.AriaRoleButton, "Authorize CLI"))
				visible(t, page.GetByText("Current password is incorrect."))
				fill(t, form.Locator(`[name="password"]`), admin.Password)
				click(t, byRole(form, *playwright.AriaRoleButton, "Authorize CLI"))
				visible(t, page.GetByText("CLI authorized", playwright.PageGetByTextOptions{Exact: new(true)}))
			} else {
				click(t, byRole(form, *playwright.AriaRoleButton, "Deny request"))
				visible(t, page.GetByText("Request denied", playwright.PageGetByTextOptions{Exact: new(true)}))
			}
			// Respect the advertised polling interval even on a fast browser run.
			timer := time.NewTimer(time.Duration(challenge.Interval) * time.Second)
			defer timer.Stop()
			select {
			case <-timer.C:
			case <-t.Context().Done():
				t.Fatal(t.Context().Err())
			}
			response, err = request.Post("/api/v1/dashboard/cli/token", playwright.APIRequestContextPostOptions{
				Form: map[string]any{"client_id": "terrascale-cli", "device_code": challenge.Device, "grant_type": "urn:ietf:params:oauth:grant-type:device_code"},
			})
			must(t, err)
			var token struct {
				Access string `json:"access_token"`
				Error  string `json:"error"`
			}
			if decision == "deny" {
				responseJSON(t, response, 400, &token)
				if token.Error != "access_denied" || token.Access != "" {
					t.Fatal("denial issued a credential")
				}
				return
			}
			responseJSON(t, response, 200, &token)
			if !strings.HasPrefix(token.Access, "tspat_") {
				t.Fatal("approval did not deliver a personal token")
			}
			content, err := page.Content()
			must(t, err)
			if strings.Contains(content, token.Access) || strings.Contains(content, challenge.Device) {
				t.Fatal("private device credentials appeared in the browser page")
			}
			var profile struct{ Email string }
			callTool(t, connectMCP(t, token.Access), "get_profile", nil, &profile)
			if profile.Email != admin.Email {
				t.Fatal("issued credential cannot access the approved dashboard account")
			}
		})
	}
}
