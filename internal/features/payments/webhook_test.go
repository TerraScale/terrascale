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
	"terrascale.tech/dashboard/internal/core/memstore"
	"terrascale.tech/dashboard/internal/features/payments"
	"terrascale.tech/dashboard/internal/features/subscriptions"
	"terrascale.tech/dashboard/internal/platform/terrabasetest"
	"terrascale.tech/sdk/v1"

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
func (sessions fixtureSessions) End(http.ResponseWriter, *http.Request) error     { return nil }
func (sessions fixtureSessions) RevokeUser(context.Context, string, string) error { return nil }

type billingFixture struct {
	store     core.Store
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
	store := memstore.New()
	account, err := store.CreateAccount(t.Context(), core.User{Name: "Owner", Email: "billing@example.test", PasswordHash: "unused"})
	if err != nil {
		t.Fatal(err)
	}
	principal := account.Principal()
	service := subscriptions.NewBillingService(store)
	if _, err := state.BindCustomer(t.Context(), principal.OrganizationID, "cus_test"); err != nil {
		t.Fatal(err)
	}
	if _, err := service.RecordCustomer(t.Context(), principal, "cus_test"); err != nil {
		t.Fatal(err)
	}
	if err := service.RecordExternalCheckout(t.Context(), principal, payments.PlanPro, payments.IntervalMonthly, "/usage?tab=billing", payments.CheckoutSession{
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
	account, err := fixture.store.SnapshotAs(t.Context(), fixture.principal)
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
	NoCustomer bool
	APIVersion string
	Livemode   bool
}

func (fixture billingFixture) deliver(t *testing.T, event testEvent, secret string) *httptest.ResponseRecorder {
	t.Helper()
	object := map[string]any{"id": "obj_" + event.ID, "customer": cmp.Or(event.Customer, "cus_test")}
	if event.NoCustomer {
		delete(object, "customer")
	}
	payload, err := json.Marshal(map[string]any{
		"id": event.ID, "object": "event", "type": event.Type, "created": time.Now().Unix(),
		"api_version": cmp.Or(event.APIVersion, stripe.APIVersion), "livemode": event.Livemode,
		"data": map[string]any{"object": object},
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
	// Only the time of the latest Stripe read may change.
	again := fixture.organization(t)
	again.Billing.SyncedAt = first.Billing.SyncedAt
	if !reflect.DeepEqual(first, again) {
		t.Fatalf("replayed and older events changed the organization:\nfirst %#v\nagain %#v", first.Billing, again.Billing)
	}
}

func TestWebhookSkipsUnboundAndUntrackedEvents(t *testing.T) {
	fixture := newBillingFixture(t)
	for _, event := range []testEvent{
		{ID: "evt_other", Type: "invoice.paid", Customer: "cus_other"},
		{ID: "evt_untracked", Type: "customer.updated"},
		{ID: "evt_guest", Type: "checkout.session.completed", NoCustomer: true},
	} {
		if response := fixture.deliver(t, event, testWebhookSecret); response.Code != http.StatusOK {
			t.Fatalf("%s = %d", event.ID, response.Code)
		}
	}
	if fixture.provider.fetches != 0 || fixture.organization(t).Billing.CheckoutID != "cs_test" {
		t.Fatalf("skipped events read Stripe %d times or changed billing", fixture.provider.fetches)
	}
}

func TestWebhookTerraBaseDistinguishesMissingCustomerFromStorageErrors(t *testing.T) {
	if testing.Short() {
		t.Skip("starts pinned TerraBase release")
	}
	server := terrabasetest.Start(t)
	for _, test := range []struct {
		name   string
		status int
	}{
		{"missing_document", http.StatusOK},
		{"missing_collection", http.StatusInternalServerError},
		{"missing_database", http.StatusInternalServerError},
	} {
		t.Run(test.name, func(t *testing.T) {
			db, key := terrabasetest.Database(t, server, "dashboard")
			switch test.name {
			case "missing_document":
				for _, collection := range []string{"stripe_customers", "stripe_organizations"} {
					if _, err := db.CreateCollection(t.Context(), collection); err != nil {
						t.Fatal(err)
					}
				}
			case "missing_database":
				client, err := v1.New(v1.Config{BaseURL: server.URL, Token: key, HTTPClient: server.Client})
				if err != nil {
					t.Fatal(err)
				}
				db = client.Database(db.Project(), "missing")
			}
			fixture := newBillingFixture(t)
			before := fixture.organization(t)
			fixture.state = payments.NewTerraBaseStateStoreForTest(db)
			fixture.billing = payments.NewStripeForTest(testConfig, fixture.provider, fixture.state)
			fixture.mux = http.NewServeMux()
			payments.Mount(fixture.mux, core.Deps{Store: fixture.store, Sessions: fixtureSessions{principal: fixture.principal}}, fixture.service, fixture.billing)
			for range 2 {
				response := fixture.deliver(t, testEvent{ID: "evt_storage", Type: "invoice.paid", Customer: "cus_missing"}, testWebhookSecret)
				if response.Code != test.status {
					t.Errorf("webhook = %d %s; want %d", response.Code, response.Body, test.status)
				}
			}
			if fixture.provider.fetches != 0 {
				t.Errorf("webhook read Stripe %d times; want 0", fixture.provider.fetches)
			}
			if !reflect.DeepEqual(before, fixture.organization(t)) {
				t.Error("webhook changed organization billing")
			}
		})
	}
}

func TestWebhookSkipsCustomerOfMissingOrganization(t *testing.T) {
	fixture := newBillingFixture(t)
	if _, err := fixture.state.BindCustomer(t.Context(), "org_deleted", "cus_deleted"); err != nil {
		t.Fatal(err)
	}
	fixture.provider.customer.OrganizationID = "org_deleted"
	if response := fixture.deliver(t, testEvent{ID: "evt_deleted", Type: "invoice.paid", Customer: "cus_deleted"}, testWebhookSecret); response.Code != http.StatusOK {
		t.Fatalf("event for a missing organization = %d, want 200", response.Code)
	}
}

func TestWebhookAsksStripeToRetryFailedSync(t *testing.T) {
	fixture := newBillingFixture(t)
	before := fixture.organization(t)
	fixture.provider.fetchErr = errors.New("Stripe is unavailable")
	if response := fixture.deliver(t, testEvent{ID: "evt_retry", Type: "invoice.paid"}, testWebhookSecret); response.Code != http.StatusInternalServerError {
		t.Fatalf("failed sync = %d, want 500", response.Code)
	}
	if !reflect.DeepEqual(before, fixture.organization(t)) {
		t.Fatal("failed delivery changed organization billing")
	}
	fixture.provider.fetchErr = nil
	for range 2 {
		if response := fixture.deliver(t, testEvent{ID: "evt_retry", Type: "invoice.paid"}, testWebhookSecret); response.Code != http.StatusOK {
			t.Fatalf("redelivery = %d, want 200", response.Code)
		}
	}
	if org := fixture.organization(t); org.PlanID != "plan_pro" || len(org.Billing.Invoices) != 1 {
		t.Fatalf("redeliveries did not converge: %#v", org.Billing)
	}
}

func TestWebhookLiveMode(t *testing.T) {
	fixture := newBillingFixture(t)
	config := testConfig
	config.Mode = payments.ModeLive
	fixture.billing = payments.NewStripeForTest(config, fixture.provider, fixture.state)
	fixture.mux = http.NewServeMux()
	payments.Mount(fixture.mux, core.Deps{Store: fixture.store, Sessions: fixtureSessions{principal: fixture.principal}}, fixture.service, fixture.billing)
	if response := fixture.deliver(t, testEvent{ID: "evt_test", Type: "invoice.paid"}, testWebhookSecret); response.Code != http.StatusBadRequest || fixture.provider.fetches != 0 {
		t.Fatalf("test event in live mode = %d, Stripe reads %d", response.Code, fixture.provider.fetches)
	}
	for range 2 {
		if response := fixture.deliver(t, testEvent{ID: "evt_live", Type: "invoice.paid", Livemode: true}, testWebhookSecret); response.Code != http.StatusOK {
			t.Fatalf("live delivery = %d", response.Code)
		}
	}
	if org := fixture.organization(t); org.PlanID != "plan_pro" || len(org.Billing.Invoices) != 1 {
		t.Fatalf("duplicate live deliveries did not converge: %#v", org.Billing)
	}
}

func TestWebhookRejectsInvalidRawSignatures(t *testing.T) {
	payload := []byte(`{"id":"evt_signed","object":"event","type":"invoice.paid","api_version":"` + stripe.APIVersion + `","data":{"object":{"customer":"cus_test"}}}`)
	valid := webhook.GenerateTestSignedPayload(&webhook.UnsignedPayload{Payload: payload, Secret: testWebhookSecret})
	expired := webhook.GenerateTestSignedPayload(&webhook.UnsignedPayload{Payload: payload, Secret: testWebhookSecret, Timestamp: time.Now().Add(-6 * time.Minute)})
	for _, test := range []struct {
		name, header string
		body         []byte
	}{
		{"missing header", "", payload},
		{"malformed header", "t=invalid,v1=invalid", payload},
		{"expired signature", expired.Header, payload},
		{"tampered body", valid.Header, append(bytes.Clone(payload), ' ')},
	} {
		t.Run(test.name, func(t *testing.T) {
			fixture := newBillingFixture(t)
			request := httptest.NewRequest(http.MethodPost, payments.WebhookPath, bytes.NewReader(test.body))
			request.Header.Set("Stripe-Signature", test.header)
			response := httptest.NewRecorder()
			fixture.mux.ServeHTTP(response, request)
			if response.Code != http.StatusBadRequest || fixture.provider.fetches != 0 {
				t.Fatalf("untrusted delivery = %d, Stripe reads %d", response.Code, fixture.provider.fetches)
			}
		})
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
	store := memstore.New()
	payments.Mount(mux, core.Deps{Store: store, Demo: true}, subscriptions.NewBillingService(store), nil)
	response := httptest.NewRecorder()
	mux.ServeHTTP(response, httptest.NewRequest(http.MethodPost, payments.WebhookPath, bytes.NewReader([]byte("{}"))))
	if response.Code != http.StatusServiceUnavailable {
		t.Fatalf("demo webhook = %d", response.Code)
	}
}
