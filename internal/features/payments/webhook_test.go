package payments_test

import (
	"bytes"
	"cmp"
	"context"
	"encoding/json/v2"
	"net/http"
	"net/http/httptest"
	"testing"
	"time"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/features/payments"
	"terrascale.tech/dashboard/internal/features/subscriptions"

	stripe "github.com/stripe/stripe-go/v86"
	"github.com/stripe/stripe-go/v86/webhook"
)

const testWebhookSecret = "whsec_test"

type fakeProvider struct {
	payments.BillingProvider
	subscription  payments.SubscriptionSnapshot
	subscriptions int
}

func (provider *fakeProvider) RetrieveSubscription(context.Context, string) (payments.SubscriptionSnapshot, error) {
	provider.subscriptions++
	return provider.subscription, nil
}

type webhookFixture struct {
	store     *core.Store
	principal core.Principal
	provider  *fakeProvider
	mux       *http.ServeMux
}

// newWebhookFixture creates an owner whose organization has a Stripe customer
// and an open Checkout Session for Pro monthly, as after POST /billing/checkout.
func newWebhookFixture(t *testing.T) webhookFixture {
	t.Helper()
	store := core.NewStore()
	account, err := store.CreateAccount(core.User{Name: "Owner", Email: "webhook@example.test", PasswordHash: "unused"})
	if err != nil {
		t.Fatal(err)
	}
	principal := account.Principal()
	service := subscriptions.NewBillingService(store)
	if _, err := service.RecordCustomer(principal, "cus_test"); err != nil {
		t.Fatal(err)
	}
	if err := service.RecordExternalCheckout(principal, payments.PlanPro, payments.IntervalMonthly, "/billing", payments.CheckoutSession{
		ID: "cs_test", CustomerID: "cus_test", ClientReferenceID: principal.OrganizationID, ExpiresAt: time.Now().Add(time.Hour),
	}); err != nil {
		t.Fatal(err)
	}
	provider := &fakeProvider{subscription: payments.SubscriptionSnapshot{
		ID: "sub_test", CustomerID: "cus_test", OrganizationID: principal.OrganizationID, Status: "active",
		PriceID: "price_pro_monthly", ItemID: "si_test", CurrentPeriodEnd: time.Now().Add(30 * 24 * time.Hour).UTC(),
	}}
	config := payments.StripeConfig{
		Mode: payments.ModeTest, WebhookSecret: testWebhookSecret, PublicOrigin: "https://dashboard.example.test",
		PriceIDs: map[payments.PriceKey]string{{PlanKey: payments.PlanPro, Interval: payments.IntervalMonthly}: "price_pro_monthly"},
	}
	mux := http.NewServeMux()
	payments.Mount(mux, core.Deps{Store: store}, service, payments.NewStripeForTest(config, provider))
	return webhookFixture{store: store, principal: principal, provider: provider, mux: mux}
}

func (fixture webhookFixture) organization(t *testing.T) core.Organization {
	t.Helper()
	account, err := fixture.store.SnapshotAs(fixture.principal)
	if err != nil {
		t.Fatal(err)
	}
	return account.Organizations[fixture.principal.OrganizationID]
}

type testEvent struct {
	ID         string
	Type       string
	Object     map[string]any
	APIVersion string
	Livemode   bool
}

func (fixture webhookFixture) deliver(t *testing.T, event testEvent, secret string) *httptest.ResponseRecorder {
	t.Helper()
	payload, err := json.Marshal(map[string]any{
		"id": event.ID, "object": "event", "type": event.Type, "created": time.Now().Unix(),
		"api_version": cmp.Or(event.APIVersion, stripe.APIVersion), "livemode": event.Livemode,
		"data": map[string]any{"object": event.Object},
	})
	if err != nil {
		t.Fatal(err)
	}
	signed := webhook.GenerateTestSignedPayload(&webhook.UnsignedPayload{Payload: payload, Secret: secret})
	request := httptest.NewRequest(http.MethodPost, payments.WebhookPath, bytes.NewReader(payload))
	request.Header.Set("Stripe-Signature", signed.Header)
	response := httptest.NewRecorder()
	fixture.mux.ServeHTTP(response, request)
	return response
}

func invoiceObject(id, status string) map[string]any {
	now := time.Now().Unix()
	return map[string]any{
		"id": id, "object": "invoice", "customer": "cus_test", "status": status, "number": "INV-1",
		"amount_due": 5000, "currency": "usd", "created": now, "period_start": now, "period_end": now + 30*24*3600,
		"parent": map[string]any{"type": "subscription_details", "subscription_details": map[string]any{"subscription": "sub_test"}},
	}
}

func checkoutObject(orgID, status, paymentStatus string) map[string]any {
	return map[string]any{
		"id": "cs_test", "object": "checkout.session", "mode": "subscription", "customer": "cus_test",
		"subscription": "sub_test", "client_reference_id": orgID, "status": status, "payment_status": paymentStatus,
		"metadata": map[string]string{"organization_id": orgID, "plan_key": payments.PlanPro, "interval": payments.IntervalMonthly},
	}
}

func TestWebhookRecordsInvoiceDeliveredBeforeCheckoutCompletion(t *testing.T) {
	fixture := newWebhookFixture(t)
	response := fixture.deliver(t, testEvent{ID: "evt_invoice", Type: "invoice.paid", Object: invoiceObject("in_first", "paid")}, testWebhookSecret)
	if response.Code != http.StatusOK {
		t.Fatalf("invoice webhook = %d %s", response.Code, response.Body)
	}
	invoices := fixture.organization(t).Billing.Invoices
	if len(invoices) != 1 || invoices[0].ID != "in_first" || invoices[0].Status != "paid" || invoices[0].AmountCents != 5000 {
		t.Fatalf("invoices = %#v", invoices)
	}
	if fixture.provider.subscriptions != 0 {
		t.Fatalf("invoice webhook retrieved the subscription %d times", fixture.provider.subscriptions)
	}
}

func TestWebhookActivatesCheckoutFromSubscriptionSnapshot(t *testing.T) {
	fixture := newWebhookFixture(t)
	orgID := fixture.principal.OrganizationID
	response := fixture.deliver(t, testEvent{ID: "evt_checkout", Type: "checkout.session.completed", Object: checkoutObject(orgID, "complete", "paid")}, testWebhookSecret)
	if response.Code != http.StatusOK {
		t.Fatalf("checkout webhook = %d %s", response.Code, response.Body)
	}
	org := fixture.organization(t)
	if org.Billing.SubscriptionID != "sub_test" || org.Billing.PlanKey != payments.PlanPro || org.Billing.Status != "active" || org.Billing.CheckoutID != "" || org.PlanID != "plan_pro" {
		t.Fatalf("billing after checkout = %#v", org.Billing)
	}
	if !org.BillingRenewal.Equal(fixture.provider.subscription.CurrentPeriodEnd) {
		t.Fatalf("renewal = %v, want %v", org.BillingRenewal, fixture.provider.subscription.CurrentPeriodEnd)
	}
	if replay := fixture.deliver(t, testEvent{ID: "evt_checkout", Type: "checkout.session.completed", Object: checkoutObject(orgID, "complete", "paid")}, testWebhookSecret); replay.Code != http.StatusOK {
		t.Fatalf("replayed webhook = %d", replay.Code)
	}
}

func TestWebhookReleasesExpiredCheckout(t *testing.T) {
	fixture := newWebhookFixture(t)
	orgID := fixture.principal.OrganizationID
	object := checkoutObject(orgID, "expired", "unpaid")
	delete(object, "subscription")
	response := fixture.deliver(t, testEvent{ID: "evt_expired", Type: "checkout.session.expired", Object: object}, testWebhookSecret)
	if response.Code != http.StatusOK {
		t.Fatalf("expired webhook = %d %s", response.Code, response.Body)
	}
	org := fixture.organization(t)
	if org.Billing.CheckoutID != "" || org.CheckoutSessions["cs_test"].Status != "canceled" {
		t.Fatalf("checkout after expiry = %#v, sessions = %#v", org.Billing, org.CheckoutSessions)
	}
}

func TestWebhookRejectsUntrustedEvents(t *testing.T) {
	tests := []struct {
		name   string
		event  testEvent
		secret string
	}{
		{"wrong signing secret", testEvent{ID: "evt_bad", Type: "invoice.paid", Object: invoiceObject("in_bad", "paid")}, "whsec_other"},
		{"old API version", testEvent{ID: "evt_old", Type: "invoice.paid", Object: invoiceObject("in_old", "paid"), APIVersion: "2024-06-20"}, testWebhookSecret},
		{"live event in test mode", testEvent{ID: "evt_live", Type: "invoice.paid", Object: invoiceObject("in_live", "paid"), Livemode: true}, testWebhookSecret},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			fixture := newWebhookFixture(t)
			if response := fixture.deliver(t, test.event, test.secret); response.Code != http.StatusBadRequest {
				t.Fatalf("webhook = %d, want 400", response.Code)
			}
			if invoices := fixture.organization(t).Billing.Invoices; len(invoices) != 0 {
				t.Fatalf("untrusted event changed invoices: %#v", invoices)
			}
		})
	}
}

func TestWebhookUnavailableInDemoMode(t *testing.T) {
	mux := http.NewServeMux()
	store := core.NewStore()
	payments.Mount(mux, core.Deps{Store: store, Demo: true}, subscriptions.NewBillingService(store), nil)
	response := httptest.NewRecorder()
	mux.ServeHTTP(response, httptest.NewRequest(http.MethodPost, payments.WebhookPath, bytes.NewReader([]byte("{}"))))
	if response.Code != http.StatusServiceUnavailable {
		t.Fatalf("demo webhook = %d", response.Code)
	}
}
