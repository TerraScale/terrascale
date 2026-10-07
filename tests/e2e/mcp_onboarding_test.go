package e2e

import (
	"net/url"
	"strings"
	"testing"
)

// TestMCPOnboardingFinishesAccountSetup walks a registered account through
// the payment and project steps with the onboarding tools.
func TestMCPOnboardingFinishesAccountSetup(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	registerAndOpenPaymentStep(t, page, newAccount("mcp-onboarding"))
	session := connectMCP(t, createAccessToken(t, page, journeyPassword, "onboarding"))

	type status struct {
		Completed bool
		Step      string
		Steps     []struct{ Key, State string }
		Plans     []struct{ Key string }
	}
	var current status
	callTool(t, session, "get_onboarding", nil, &current)
	if current.Completed || current.Step != "payment-method" || len(current.Steps) != 3 || len(current.Plans) == 0 {
		t.Fatalf("get_onboarding = %+v, want the payment step with paid plans", current)
	}

	if message := callToolError(t, session, "save_onboarding_step", map[string]any{"step": "project", "name": "Early", "region": "GRU"}); !strings.Contains(message, "current setup step first") {
		t.Errorf("out-of-order project step error = %q", message)
	}
	type result struct {
		Completed   bool
		Step        string
		CheckoutURL string
	}
	var checkout result
	callTool(t, session, "save_onboarding_step", map[string]any{"step": "payment-method", "plan": "starter", "interval": "monthly"}, &checkout)
	if !strings.HasPrefix(checkout.CheckoutURL, "/checkout/") || checkout.Step != "payment-method" {
		t.Errorf("paid payment step = %+v, want a checkout to finish in the browser", checkout)
	}
	var free result
	callTool(t, session, "save_onboarding_step", map[string]any{"step": "payment-method", "continueOnFree": true}, &free)
	if free.Step != "project" || free.Completed {
		t.Fatalf("free payment step = %+v, want the project step next", free)
	}
	if message := callToolError(t, session, "save_onboarding_step", map[string]any{"step": "project", "name": "MCP project", "region": "XXX"}); !strings.Contains(message, "supported project region") {
		t.Errorf("unsupported region error = %q", message)
	}
	var done result
	callTool(t, session, "save_onboarding_step", map[string]any{"step": "project", "name": "MCP project", "region": "SFO"}, &done)
	if !done.Completed || done.Step != "" {
		t.Fatalf("project step = %+v, want setup completed", done)
	}
	callTool(t, session, "get_onboarding", nil, &current)
	if !current.Completed {
		t.Errorf("get_onboarding after setup = %+v, want completed", current)
	}
	goTo(t, page, "/onboarding")
	waitForPath(t, page, func(u *url.URL) bool { return u.Path == "/" })
}
