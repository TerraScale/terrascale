package e2e

import (
	"net/url"
	"slices"
	"strings"
	"testing"

	"github.com/modelcontextprotocol/go-sdk/mcp"
	"github.com/mxschmitt/playwright-go"
)

type mcpBilling struct {
	PlanKey            string
	HasSubscription    bool
	CheckoutInProgress bool
	CanManage          bool
	ScheduledChange    *struct{ PlanKey, Interval string }
}

// TestMCPBillingCheckoutPlanChangesAndPortal subscribes a fresh Personal
// workspace through an MCP-started checkout the owner confirms in the browser,
// then changes, schedules, and undoes plan changes through MCP tools.
func TestMCPBillingCheckoutPlanChangesAndPortal(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	registerAndOnboard(t, page, newAccount("mcp-billing"))
	session := connectMCP(t, createAccessToken(t, page, journeyPassword, "billing assistant"))

	type plan struct {
		Key               string
		MonthlyPriceCents int64
	}
	var plans struct {
		CurrentPlanKey string
		Plans          []plan
	}
	callTool(t, session, "list_plans", nil, &plans)
	for _, key := range []string{"starter", "pro", "business"} {
		if !slices.ContainsFunc(plans.Plans, func(p plan) bool { return p.Key == key && p.MonthlyPriceCents > 0 }) {
			t.Errorf("list_plans = %+v, want priced plan %s", plans.Plans, key)
		}
	}
	if plans.CurrentPlanKey != "free" {
		t.Errorf("current plan = %q, want free", plans.CurrentPlanKey)
	}

	billing := getMCPBilling(t, session)
	if billing.HasSubscription || !billing.CanManage {
		t.Fatalf("fresh get_billing = %+v, want an unsubscribed workspace the owner manages", billing)
	}
	if message := callToolError(t, session, "change_plan", map[string]any{"plan": "pro", "interval": "monthly"}); !strings.Contains(message, "checkout first") {
		t.Errorf("change_plan without subscription = %q, want a checkout hint", message)
	}
	if message := callToolError(t, session, "create_checkout", map[string]any{"plan": "enterprise", "interval": "monthly"}); !strings.Contains(message, "unavailable") {
		t.Errorf("create_checkout for an unknown plan = %q, want plan unavailable", message)
	}

	var checkout struct{ CheckoutURL string }
	callTool(t, session, "create_checkout", map[string]any{"plan": "starter", "interval": "monthly"}, &checkout)
	if !strings.HasPrefix(checkout.CheckoutURL, "/checkout/cs_local_") {
		t.Fatalf("checkout URL = %q, want the local demo checkout page", checkout.CheckoutURL)
	}
	billing = getMCPBilling(t, session)
	if !billing.CheckoutInProgress {
		t.Error("get_billing must report the checkout in progress")
	}
	callTool(t, session, "cancel_checkout", nil, nil)
	billing = getMCPBilling(t, session)
	if billing.CheckoutInProgress || billing.HasSubscription {
		t.Errorf("after cancel_checkout billing = %+v, want no checkout and no subscription", billing)
	}

	callTool(t, session, "create_checkout", map[string]any{"plan": "starter", "interval": "monthly"}, &checkout)
	goTo(t, page, checkout.CheckoutURL)
	click(t, pageByRole(page, *playwright.AriaRoleButton, "Confirm local demo setup"))
	waitForPath(t, page, func(u *url.URL) bool { return u.Path == "/organization" && u.Query().Get("payment") == "success" })
	billing = getMCPBilling(t, session)
	if !billing.HasSubscription || billing.PlanKey != "starter" {
		t.Fatalf("after browser confirmation billing = %+v, want a starter subscription", billing)
	}
	if message := callToolError(t, session, "create_checkout", map[string]any{"plan": "pro", "interval": "monthly"}); !strings.Contains(message, "already has a subscription") {
		t.Errorf("second create_checkout = %q, want the managed subscription error", message)
	}

	var preview struct {
		Immediate bool
		Note      string
	}
	callTool(t, session, "preview_plan_change", map[string]any{"plan": "pro", "interval": "monthly"}, &preview)
	if !preview.Immediate || !strings.Contains(preview.Note, "Demo") {
		t.Errorf("preview_plan_change = %+v, want an immediate demo upgrade", preview)
	}
	var changed struct{ Outcome string }
	callTool(t, session, "change_plan", map[string]any{"plan": "pro", "interval": "monthly"}, &changed)
	if changed.Outcome != "changed" {
		t.Fatalf("upgrade outcome = %q, want changed", changed.Outcome)
	}
	callTool(t, session, "change_plan", map[string]any{"plan": "starter", "interval": "monthly"}, &changed)
	billing = getMCPBilling(t, session)
	if changed.Outcome != "scheduled" || billing.PlanKey != "pro" || billing.ScheduledChange == nil || billing.ScheduledChange.PlanKey != "starter" {
		t.Fatalf("downgrade outcome %q with billing %+v, want starter scheduled while pro stays active", changed.Outcome, billing)
	}
	callTool(t, session, "undo_plan_change", nil, &changed)
	billing = getMCPBilling(t, session)
	if changed.Outcome != "undone" || billing.ScheduledChange != nil {
		t.Errorf("undo outcome %q with billing %+v, want the scheduled change cleared", changed.Outcome, billing)
	}

	var portal struct{ URL string }
	callTool(t, session, "get_billing_portal_url", nil, &portal)
	if portal.URL != "/billing/portal/local" {
		t.Errorf("portal URL = %q, want the local demo portal", portal.URL)
	}
	var invoices struct{ Total, Page int }
	callTool(t, session, "list_invoices", nil, &invoices)
	if invoices.Total != 0 || invoices.Page != 1 {
		t.Errorf("fresh list_invoices = %+v, want an empty first page", invoices)
	}
	if message := callToolError(t, session, "list_invoices", map[string]any{"status": "refunded"}); !strings.Contains(message, "status") {
		t.Errorf("invalid invoice status = %q, want a status error", message)
	}
}

// TestMCPCheckoutOutsideSelectedOrganization confirms in the browser a
// checkout MCP started for an organization other than the dashboard selection.
func TestMCPCheckoutOutsideSelectedOrganization(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	registerAndOnboard(t, page, newAccount("mcp-billing-scope"))
	session := connectMCP(t, createAccessToken(t, page, journeyPassword, "billing scope assistant"))
	var current mcpContext
	callTool(t, session, "get_context", nil, &current)
	personalID := current.Selection.OrganizationID
	var business mcpOrganization
	callTool(t, session, "create_organization", map[string]any{"name": uniqueName("Elsewhere")}, &business)
	callTool(t, session, "select_organization", map[string]any{"organizationId": business.ID}, nil)

	personal := map[string]any{"organizationId": personalID, "plan": "starter", "interval": "monthly"}
	var checkout struct{ CheckoutURL string }
	callTool(t, session, "create_checkout", personal, &checkout)
	goTo(t, page, checkout.CheckoutURL)
	click(t, pageByRole(page, *playwright.AriaRoleButton, "Confirm local demo setup"))
	waitForPath(t, page, func(u *url.URL) bool { return u.Query().Get("payment") == "success" })
	var billing mcpBilling
	callTool(t, session, "get_billing", map[string]any{"organizationId": personalID}, &billing)
	if !billing.HasSubscription || billing.PlanKey != "starter" {
		t.Fatalf("personal billing after confirmation = %+v, want a starter subscription", billing)
	}
}

// getMCPBilling decodes get_billing into a fresh value, so fields the
// response omits read as empty.
func getMCPBilling(t *testing.T, session *mcp.ClientSession) mcpBilling {
	t.Helper()
	var billing mcpBilling
	callTool(t, session, "get_billing", nil, &billing)
	return billing
}

// TestMCPListInvoicesFiltersSeededHistory pages and filters the sample
// account's seeded invoices.
func TestMCPListInvoicesFiltersSeededHistory(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	login(t, page, sampleAccount.Email, sampleAccount.Password)
	session := connectMCP(t, createAccessToken(t, page, sampleAccount.Password, "invoice reader"))

	type invoice struct{ Number, Status string }
	var all struct {
		Invoices []invoice
		Total    int
	}
	callTool(t, session, "list_invoices", nil, &all)
	if all.Total == 0 || len(all.Invoices) == 0 || len(all.Invoices) > 10 {
		t.Fatalf("list_invoices = %+v, want a first page of seeded invoices", all)
	}
	var paid struct {
		Invoices []invoice
		Total    int
	}
	callTool(t, session, "list_invoices", map[string]any{"status": "paid"}, &paid)
	if paid.Total == 0 || paid.Total > all.Total || slices.ContainsFunc(paid.Invoices, func(i invoice) bool { return i.Status != "paid" }) {
		t.Errorf("paid invoices = %+v of %d, want only paid invoices", paid, all.Total)
	}
}
