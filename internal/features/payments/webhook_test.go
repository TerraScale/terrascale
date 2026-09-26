package payments_test

import (
	"bytes"
	"cmp"
	"context"
	"encoding/json/v2"
	"errors"
	"net/http"
	"net/http/httptest"
	"reflect"
	"testing"
	"time"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/features/payments"
	"terrascale.tech/dashboard/internal/features/subscriptions"

	stripe "github.com/stripe/stripe-go/v86"
	"github.com/stripe/stripe-go/v86/webhook"
)

const testWebhookSecret = "whsec_test"

var testPeriodEnd = time.Date(2026, time.October, 26, 0, 0, 0, 0, time.UTC)

// fakeProvider serves a Stripe customer, its pending Checkout Session, and
// the subscription the plan-change routes verify.
type fakeProvider struct {
	payments.BillingProvider
	customer     payments.StripeCustomer
	checkout     payments.CheckoutSession
	subscription payments.SubscriptionSnapshot
	fetchErr     error
	fetches      int
	releases     int
}

func (provider *fakeProvider) FetchCustomer(_ context.Context, customerID string) (payments.StripeCustomer, error) {
	provider.fetches++
	if provider.fetchErr != nil {
		return payments.StripeCustomer{}, provider.fetchErr
	}
	customer := provider.customer
	customer.ID = customerID
	return customer, nil
}

func (provider *fakeProvider) RetrieveCheckoutSession(context.Context, string) (payments.CheckoutSession, error) {
	return provider.checkout, nil
}

func (provider *fakeProvider) RetrieveSubscription(context.Context, string) (payments.SubscriptionSnapshot, error) {
	return provider.subscription, nil
}

func (provider *fakeProvider) ReleaseSchedule(context.Context, string, string) error {
	provider.releases++
	return nil
}

type fixtureSessions struct{ principal core.Principal }

func (sessions fixtureSessions) Start(http.ResponseWriter, *http.Request, core.Principal) error {
	return nil
}
func (sessions fixtureSessions) Principal(*http.Request) (core.Principal, bool) {
	return sessions.principal, true
}
func (sessions fixtureSessions) BearerPrincipal(*http.Request) (core.Principal, bool) {
	return core.Principal{}, false
}
func (sessions fixtureSessions) End(http.ResponseWriter, *http.Request) {}
func (sessions fixtureSessions) RevokeUser(string, string)              {}

type billingFixture struct {
	store     *core.Store
	principal core.Principal
	provider  *fakeProvider
	state     payments.StateStore
	billing   *payments.Stripe
	service   subscriptions.BillingService
	mux       *http.ServeMux
}

var testConfig = payments.StripeConfig{
	Mode: payments.ModeTest, WebhookSecret: testWebhookSecret, PublicOrigin: "https://dashboard.example.test",
	PriceIDs: map[payments.PriceKey]string{
		{PlanKey: payments.PlanStarter, Interval: payments.IntervalMonthly}:  "price_starter_monthly",
		{PlanKey: payments.PlanPro, Interval: payments.IntervalMonthly}:      "price_pro_monthly",
		{PlanKey: payments.PlanBusiness, Interval: payments.IntervalMonthly}: "price_business_monthly",
	},
}

// newBillingFixture creates a Personal owner bound to Stripe customer
// cus_test with an open Checkout Session for Pro monthly, as after
// POST /billing/checkout. Stripe reports the checkout complete and the
// subscription active unless a test changes the fake.
func newBillingFixture(t *testing.T) billingFixture {
	t.Helper()
	return newBillingFixtureWithStore(t, payments.NewMemoryStateStore())
}

func newBillingFixtureWithStore(t *testing.T, state payments.StateStore) billingFixture {
	t.Helper()
	store := core.NewStore()
	account, err := store.CreateAccount(core.User{Name: "Owner", Email: "billing@example.test", PasswordHash: "unused"})
	if err != nil {
		t.Fatal(err)
	}
	principal := account.Principal()
	service := subscriptions.NewBillingService(store)
	if _, err := state.BindCustomer(t.Context(), principal.OrganizationID, "cus_test"); err != nil {
		t.Fatal(err)
	}
	if _, err := service.RecordCustomer(principal, "cus_test"); err != nil {
		t.Fatal(err)
	}
	if err := service.RecordExternalCheckout(principal, payments.PlanPro, payments.IntervalMonthly, "/usage?tab=billing", payments.CheckoutSession{
		ID: "cs_test", CustomerID: "cus_test", ClientReferenceID: principal.OrganizationID, ExpiresAt: time.Now().Add(time.Hour),
	}); err != nil {
		t.Fatal(err)
	}
	provider := &fakeProvider{
		customer: payments.StripeCustomer{
			OrganizationID: principal.OrganizationID,
			Subscriptions:  []payments.SubscriptionSnapshot{activeSubscription("price_pro_monthly")},
			Invoices: []core.Invoice{{
				ID: "in_first", Number: "INV-1", Status: "paid", AmountCents: 5000, Currency: "usd",
				CreatedAt: testPeriodEnd.AddDate(0, -1, 0), PeriodStart: testPeriodEnd.AddDate(0, -1, 0), PeriodEnd: testPeriodEnd,
			}},
		},
		checkout: payments.CheckoutSession{
			ID: "cs_test", CustomerID: "cus_test", ClientReferenceID: principal.OrganizationID,
			SubscriptionID: "sub_test", Status: "complete", PaymentStatus: "paid",
		},
	}
	billing := payments.NewStripeForTest(testConfig, provider, state)
	mux := http.NewServeMux()
	payments.Mount(mux, core.Deps{Store: store, Sessions: fixtureSessions{principal: principal}}, service, billing)
	return billingFixture{store: store, principal: principal, provider: provider, state: state, billing: billing, service: service, mux: mux}
}

func activeSubscription(priceID string) payments.SubscriptionSnapshot {
	return payments.SubscriptionSnapshot{
		ID: "sub_test", CustomerID: "cus_test", Status: "active", PriceID: priceID, ItemID: "si_test",
		TrialStarted: true, CreatedAt: testPeriodEnd.AddDate(0, -1, 0),
		CurrentPeriodStart: testPeriodEnd.AddDate(0, -1, 0), CurrentPeriodEnd: testPeriodEnd,
		PaymentMethod: &payments.PaymentMethod{Brand: "visa", Last4: "4242", ExpMonth: 8, ExpYear: 2028},
	}
}

func (fixture billingFixture) organization(t *testing.T) core.Organization {
	t.Helper()
	account, err := fixture.store.SnapshotAs(fixture.principal)
	if err != nil {
		t.Fatal(err)
	}
	return account.Organizations[fixture.principal.OrganizationID]
}

func (fixture billingFixture) sync(t *testing.T) {
	t.Helper()
	if _, err := fixture.billing.SyncCustomer(t.Context(), fixture.service, "cus_test"); err != nil {
		t.Fatal(err)
	}
}

func (fixture billingFixture) request(method, target string) *httptest.ResponseRecorder {
	response := httptest.NewRecorder()
	fixture.mux.ServeHTTP(response, httptest.NewRequest(method, target, nil))
	return response
}

type testEvent struct {
	ID         string
	Type       string
	Customer   string
	APIVersion string
	Livemode   bool
}

func (fixture billingFixture) deliver(t *testing.T, event testEvent, secret string) *httptest.ResponseRecorder {
	t.Helper()
	payload, err := json.Marshal(map[string]any{
		"id": event.ID, "object": "event", "type": event.Type, "created": time.Now().Unix(),
		"api_version": cmp.Or(event.APIVersion, stripe.APIVersion), "livemode": event.Livemode,
		"data": map[string]any{"object": map[string]any{"id": "obj_" + event.ID, "customer": cmp.Or(event.Customer, "cus_test")}},
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

func TestWebhookSyncsCustomerFromStripe(t *testing.T) {
	fixture := newBillingFixture(t)
	if response := fixture.deliver(t, testEvent{ID: "evt_invoice", Type: "invoice.paid"}, testWebhookSecret); response.Code != http.StatusOK {
		t.Fatalf("webhook = %d %s", response.Code, response.Body)
	}
	org := fixture.organization(t)
	if org.PlanID != "plan_pro" || org.Billing.SubscriptionID != "sub_test" || org.Billing.Status != "active" || org.BillingStatus != "active" {
		t.Fatalf("billing after sync = %#v", org.Billing)
	}
	if org.Billing.CheckoutID != "" || org.CheckoutSessions["cs_test"].Status != "complete" {
		t.Fatalf("checkout after sync = %#v, sessions = %#v", org.Billing.CheckoutID, org.CheckoutSessions)
	}
	if !org.BillingRenewal.Equal(testPeriodEnd) || len(org.Billing.Invoices) != 1 || org.Billing.Invoices[0].ID != "in_first" {
		t.Fatalf("renewal = %v, invoices = %#v", org.BillingRenewal, org.Billing.Invoices)
	}
	if org.Billing.PaymentMethodBrand != "visa" || org.Billing.PaymentMethodLastFour != "4242" || org.Billing.PaymentMethodExpiry != "08/28" {
		t.Fatalf("payment method = %#v", org.Billing)
	}
	stored, version, err := fixture.state.Load(t.Context(), "cus_test")
	if err != nil || version == "" || stored.Status != "active" || stored.PlanKey != payments.PlanPro || stored.OrganizationID != fixture.principal.OrganizationID {
		t.Fatalf("stored state = %#v, version %q, error %v", stored, version, err)
	}
}

func TestWebhookSyncIsIdempotentAcrossEventOrder(t *testing.T) {
	fixture := newBillingFixture(t)
	fixture.deliver(t, testEvent{ID: "evt_updated", Type: "customer.subscription.updated"}, testWebhookSecret)
	first := fixture.organization(t)
	for _, event := range []testEvent{
		{ID: "evt_updated", Type: "customer.subscription.updated"},
		{ID: "evt_created", Type: "customer.subscription.created"},
		{ID: "evt_checkout", Type: "checkout.session.completed"},
	} {
		if response := fixture.deliver(t, event, testWebhookSecret); response.Code != http.StatusOK {
			t.Fatalf("%s = %d", event.Type, response.Code)
		}
	}
	if again := fixture.organization(t); !reflect.DeepEqual(first, again) {
		t.Fatalf("replayed and older events changed the organization:\nfirst %#v\nagain %#v", first.Billing, again.Billing)
	}
}

func TestWebhookSkipsUnboundAndUntrackedEvents(t *testing.T) {
	fixture := newBillingFixture(t)
	for _, event := range []testEvent{
		{ID: "evt_other", Type: "invoice.paid", Customer: "cus_other"},
		{ID: "evt_untracked", Type: "customer.updated"},
	} {
		if response := fixture.deliver(t, event, testWebhookSecret); response.Code != http.StatusOK {
			t.Fatalf("%s = %d", event.ID, response.Code)
		}
	}
	if fixture.provider.fetches != 0 || fixture.organization(t).Billing.CheckoutID != "cs_test" {
		t.Fatalf("skipped events read Stripe %d times or changed billing", fixture.provider.fetches)
	}
}

func TestWebhookAsksStripeToRetryFailedSync(t *testing.T) {
	fixture := newBillingFixture(t)
	fixture.provider.fetchErr = errors.New("Stripe is unavailable")
	if response := fixture.deliver(t, testEvent{ID: "evt_retry", Type: "invoice.paid"}, testWebhookSecret); response.Code != http.StatusInternalServerError {
		t.Fatalf("failed sync = %d, want 500", response.Code)
	}
}

func TestWebhookRejectsUntrustedEvents(t *testing.T) {
	tests := []struct {
		name   string
		event  testEvent
		secret string
	}{
		{"wrong signing secret", testEvent{ID: "evt_bad", Type: "invoice.paid"}, "whsec_other"},
		{"old API version", testEvent{ID: "evt_old", Type: "invoice.paid", APIVersion: "2024-06-20"}, testWebhookSecret},
		{"live event in test mode", testEvent{ID: "evt_live", Type: "invoice.paid", Livemode: true}, testWebhookSecret},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			fixture := newBillingFixture(t)
			if response := fixture.deliver(t, test.event, test.secret); response.Code != http.StatusBadRequest {
				t.Fatalf("webhook = %d, want 400", response.Code)
			}
			if fixture.provider.fetches != 0 {
				t.Fatalf("untrusted event read Stripe %d times", fixture.provider.fetches)
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
