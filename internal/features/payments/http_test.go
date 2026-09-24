package payments

import (
	"context"
	"errors"
	"fmt"
	"net/http"
	"net/http/httptest"
	"net/url"
	"strings"
	"testing"
	"time"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/features/organization"
)

func TestDemoCheckoutRouteNeverCallsStripeProvider(t *testing.T) {
	principal := core.Principal{UserID: "usr_owner", AccountID: "acct_test", OrganizationID: "org_test"}
	flow := &testBillingFlow{}
	provider := &testProvider{}
	mux := http.NewServeMux()
	deps := core.Deps{Demo: true, Sessions: testSessions{principal: principal}}
	mount(mux, deps, flow, StripeConfig{Production: true, Mode: "live", SecretKey: "sk_live_ignored"}, provider, nil)

	form := url.Values{"plan": {"pro"}, "interval": {"annual"}, "return": {"/billing"}}
	request := httptest.NewRequest(http.MethodPost, "/billing/checkout", strings.NewReader(form.Encode()))
	request.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	response := httptest.NewRecorder()
	mux.ServeHTTP(response, request)

	if response.Code != http.StatusSeeOther || response.Header().Get("Location") != "/checkout/cs_local_test" {
		t.Fatalf("checkout response = %d %q", response.Code, response.Header().Get("Location"))
	}
	if flow.demoCheckoutCalls != 1 || provider.customerCalls != 0 || provider.checkoutCalls != 0 {
		t.Fatalf("demo checkout reached Stripe: flow=%d customer=%d checkout=%d", flow.demoCheckoutCalls, provider.customerCalls, provider.checkoutCalls)
	}
}

func TestBillingReturnPathDefaultsAndPreservesExplicitWizardPath(t *testing.T) {
	if got, err := normalizeReturnPath(""); err != nil || got != "/billing" {
		t.Fatalf("empty billing return path = %q, %v", got, err)
	}
	if got := withPaymentOutcome("invalid-url%", "success"); got != "/billing" {
		t.Fatalf("invalid billing return path = %q", got)
	}
	if got, err := normalizeReturnPath("/organization?newOrganization=project"); err != nil || got != "/organization?newOrganization=project" {
		t.Fatalf("explicit wizard return path = %q, %v", got, err)
	}
}

func TestUnauthenticatedPaymentRoutesPreserveLoginReturnTarget(t *testing.T) {
	mux := http.NewServeMux()
	mount(mux, core.Deps{Sessions: testSessions{}}, &testBillingFlow{}, StripeConfig{Demo: true}, nil, nil)
	for _, target := range []string{
		"/billing/checkout/return?session_id=cs_test_123&source=email",
		"/billing/portal?tab=invoices",
	} {
		response := httptest.NewRecorder()
		mux.ServeHTTP(response, httptest.NewRequest(http.MethodGet, target, nil))
		location, err := url.Parse(response.Header().Get("Location"))
		if response.Code != http.StatusSeeOther || err != nil || location.Path != "/login" || location.Query().Get("returnTo") != target || location.Query().Get("return") != "" {
			t.Fatalf("GET %s login redirect = %d %q, parse error = %v", target, response.Code, response.Header().Get("Location"), err)
		}
	}
}

func TestStripeWebhookRouteVerifiesSignatureAndProjectsSupportedEvents(t *testing.T) {
	principal := core.Principal{UserID: "usr_owner", AccountID: "acct_test", OrganizationID: "org_test"}
	flow := &testBillingFlow{}
	config := StripeConfig{
		Production: true, Mode: "test", WebhookSecret: "whsec_test",
		PriceIDs: map[PriceKey]string{{PlanKey: PlanPro, Interval: IntervalMonthly}: "price_pro_monthly"},
	}
	mux := http.NewServeMux()
	provider := &testProvider{organizationID: principal.OrganizationID, customerID: "cus_test"}
	mount(mux, core.Deps{Sessions: testSessions{principal: principal}}, flow, config, provider, nil)

	createdAt := time.Now().UTC().Truncate(time.Second)
	payload := []byte(fmt.Sprintf(`{"id":"evt_paid","object":"event","created":%d,"livemode":false,"type":"checkout.session.completed","data":{"object":{"id":"cs_test","object":"checkout.session","customer":"cus_test","subscription":"sub_test","client_reference_id":"org_test","status":"complete","payment_status":"paid","metadata":{"organization_id":"org_test","plan_key":"pro","interval":"monthly"}}}}`, createdAt.Unix()))
	request := httptest.NewRequest(http.MethodPost, "/api/auth/stripe/webhook", strings.NewReader(string(payload)))
	request.Header.Set("Stripe-Signature", signedHeader(payload, createdAt))
	response := httptest.NewRecorder()
	mux.ServeHTTP(response, request)
	if response.Code != http.StatusOK {
		t.Fatalf("valid webhook response = %d: %s", response.Code, response.Body.String())
	}
	if len(flow.events) != 1 {
		t.Fatalf("applied billing events = %d, want 1", len(flow.events))
	}
	event := flow.events[0]
	if event.ID != "evt_paid" || event.CheckoutID != "cs_test" || event.SubscriptionID != "sub_test" || event.PaymentStatus != "paid" || event.OrganizationID != "org_test" || !event.ProviderCurrent || event.PlanKey != PlanPro || event.Interval != IntervalMonthly {
		t.Fatalf("projected billing event = %#v", event)
	}

	request = httptest.NewRequest(http.MethodPost, "/api/auth/stripe/webhook", strings.NewReader(string(payload)))
	request.Header.Set("Stripe-Signature", signedHeader(payload, createdAt, "whsec_wrong"))
	response = httptest.NewRecorder()
	mux.ServeHTTP(response, request)
	if response.Code != http.StatusBadRequest || len(flow.events) != 1 {
		t.Fatalf("invalid signature response = %d, applied events = %d", response.Code, len(flow.events))
	}
}

func TestStripeWebhookSubscriptionLookupFailureRequestsRetry(t *testing.T) {
	createdAt := time.Now().UTC().Truncate(time.Second)
	payload := []byte(fmt.Sprintf(`{"id":"evt_retry","object":"event","created":%d,"livemode":false,"type":"customer.subscription.updated","data":{"object":{"id":"sub_retry","object":"subscription","customer":"cus_retry","status":"active","metadata":{"organization_id":"org_retry","plan_key":"pro","interval":"monthly"}}}}`, createdAt.Unix()))
	flow := &testBillingFlow{}
	provider := &testProvider{customerID: "cus_retry", subscriptionErr: errors.New("provider unavailable")}
	mux := http.NewServeMux()
	mount(mux, core.Deps{}, flow, StripeConfig{Production: true, Mode: "test", WebhookSecret: "whsec_test"}, provider, nil)
	request := httptest.NewRequest(http.MethodPost, "/api/auth/stripe/webhook", strings.NewReader(string(payload)))
	request.Header.Set("Stripe-Signature", signedHeader(payload, createdAt))
	response := httptest.NewRecorder()
	mux.ServeHTTP(response, request)
	if response.Code != http.StatusBadGateway || len(flow.events) != 0 {
		t.Fatalf("unavailable subscription response = %d, applied events = %d", response.Code, len(flow.events))
	}
}

func TestStripeWebhookRejectsOversizedBody(t *testing.T) {
	flow := &testBillingFlow{}
	mux := http.NewServeMux()
	mount(mux, core.Deps{}, flow, StripeConfig{Production: true, Mode: "test", WebhookSecret: "whsec_test"}, nil, nil)
	request := httptest.NewRequest(http.MethodPost, "/api/auth/stripe/webhook", strings.NewReader(strings.Repeat("x", maxStripeWebhookBody+1)))
	response := httptest.NewRecorder()
	mux.ServeHTTP(response, request)
	if response.Code != http.StatusRequestEntityTooLarge {
		t.Fatalf("oversized webhook response = %d", response.Code)
	}
}

func TestExistingStripeSubscriptionUsesPortalInsteadOfCreatingDuplicate(t *testing.T) {
	store := core.NewStore()
	account, err := store.CreateAccount(core.User{Email: "owner@example.test", Name: "Owner", PasswordHash: "test-hash", Role: "owner"})
	if err != nil {
		t.Fatalf("CreateAccount() error = %v", err)
	}
	created, err := organization.NewService(store).CreateBusiness(account.Principal(), "Billing Labs")
	if err != nil {
		t.Fatalf("CreateBusiness() error = %v", err)
	}
	account, ok := store.Snapshot(account.ID)
	if !ok {
		t.Fatal("Snapshot() did not find created account")
	}
	principal := account.Principal()
	if err := store.UpdateAs(principal, func(account *core.Account) error {
		org := account.Organizations[created.ID]
		org.Billing.CustomerID = "cus_existing"
		org.Billing.SubscriptionID = "sub_existing"
		org.Billing.Status = "active"
		org.BillingStatus = "active"
		org.PaymentMethodValidated = true
		account.Organizations[org.ID] = org
		return nil
	}); err != nil {
		t.Fatalf("prepare existing subscription: %v", err)
	}
	flow := &testBillingFlow{}
	provider := &testProvider{organizationID: created.ID, customerID: "cus_existing"}
	mux := http.NewServeMux()
	mount(mux, core.Deps{Store: store, Sessions: testSessions{principal: principal}}, flow,
		StripeConfig{
			Production: true, Mode: "test", PublicOrigin: "https://dashboard.example",
			PriceIDs: map[PriceKey]string{{PlanKey: PlanPro, Interval: IntervalMonthly}: "price_pro_monthly"},
		}, provider, nil)
	form := url.Values{"plan": {"starter"}, "interval": {"monthly"}, "return": {"/billing"}}
	request := httptest.NewRequest(http.MethodPost, "/billing/checkout", strings.NewReader(form.Encode()))
	request.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	response := httptest.NewRecorder()
	mux.ServeHTTP(response, request)
	if response.Code != http.StatusSeeOther || response.Header().Get("Location") != "/billing/portal" {
		t.Fatalf("existing subscription response = %d %q", response.Code, response.Header().Get("Location"))
	}
	if provider.customerCalls != 0 || provider.checkoutCalls != 0 || flow.externalCheckoutCalls != 0 {
		t.Fatalf("existing subscription started a duplicate checkout: customer=%d checkout=%d recorded=%d", provider.customerCalls, provider.checkoutCalls, flow.externalCheckoutCalls)
	}
	portalResponse := httptest.NewRecorder()
	mux.ServeHTTP(portalResponse, httptest.NewRequest(http.MethodGet, "/billing/portal", nil))
	if portalResponse.Code != http.StatusSeeOther || provider.portalReturnURL != "https://dashboard.example/billing" {
		t.Fatalf("billing portal response = %d, return URL = %q", portalResponse.Code, provider.portalReturnURL)
	}
}

func TestStripeCheckoutRejectsUnpricedCatalogPlansBeforeCustomerCreation(t *testing.T) {
	for _, planKey := range []string{"team", "scale"} {
		t.Run(planKey, func(t *testing.T) {
			store := core.NewStore()
			account, err := store.CreateAccount(core.User{Email: "owner@example.test", Name: "Owner", PasswordHash: "test-hash", Role: "owner"})
			if err != nil {
				t.Fatalf("CreateAccount() error = %v", err)
			}
			if _, err := organization.NewService(store).CreateBusiness(account.Principal(), "Billing Labs"); err != nil {
				t.Fatalf("CreateBusiness() error = %v", err)
			}
			account, ok := store.Snapshot(account.ID)
			if !ok {
				t.Fatal("Snapshot() did not find created account")
			}
			admin, err := store.CreateAccount(core.User{Email: "admin@example.test", Name: "Admin", PasswordHash: "admin-test-hash", PlatformAdmin: true})
			if err != nil {
				t.Fatalf("CreateAccount(admin) error = %v", err)
			}
			if err := store.UpsertPlanAs(admin.Principal(), core.Plan{
				ID: "plan_" + planKey, Key: planKey, Name: "Custom", PriceCents: 2000,
				AnnualPriceCents: 20000, Visible: true,
			}); err != nil {
				t.Fatalf("UpsertPlanAs(%s) error = %v", planKey, err)
			}
			principal := account.Principal()
			flow := &testBillingFlow{}
			provider := &testProvider{organizationID: principal.OrganizationID}
			mux := http.NewServeMux()
			mount(mux, core.Deps{Store: store, Sessions: testSessions{principal: principal}}, flow,
				StripeConfig{
					Production: true, Mode: "test", PublicOrigin: "https://dashboard.example",
					PriceIDs: map[PriceKey]string{{PlanKey: PlanBusiness, Interval: IntervalMonthly}: "price_business_monthly"},
				}, provider, nil)
			form := url.Values{"plan": {planKey}, "interval": {IntervalMonthly}, "return": {"/billing"}}
			request := httptest.NewRequest(http.MethodPost, "/billing/checkout", strings.NewReader(form.Encode()))
			request.Header.Set("Content-Type", "application/x-www-form-urlencoded")
			response := httptest.NewRecorder()
			mux.ServeHTTP(response, request)
			if response.Code < http.StatusBadRequest || response.Header().Get("Location") != "" {
				t.Fatalf("unpriced checkout response = %d %q", response.Code, response.Header().Get("Location"))
			}
			if provider.customerCalls != 0 || provider.checkoutCalls != 0 || flow.externalCheckoutCalls != 0 {
				t.Fatalf("unpriced plan reached Stripe: customers=%d checkouts=%d bindings=%d", provider.customerCalls, provider.checkoutCalls, flow.externalCheckoutCalls)
			}
		})
	}
}

func TestOpenStripeCheckoutRemainsAvailableAfterPlanIsHidden(t *testing.T) {
	store := core.NewStore()
	account, err := store.CreateAccount(core.User{Email: "owner@example.test", Name: "Owner", PasswordHash: "test-hash", Role: "owner"})
	if err != nil {
		t.Fatalf("CreateAccount() error = %v", err)
	}
	created, err := organization.NewService(store).CreateBusiness(account.Principal(), "Billing Labs")
	if err != nil {
		t.Fatalf("CreateBusiness() error = %v", err)
	}
	account, ok := store.Snapshot(account.ID)
	if !ok {
		t.Fatal("Snapshot() did not find created account")
	}
	principal := account.Principal()
	admin, err := store.CreateAccount(core.User{Email: "admin@example.test", Name: "Admin", PasswordHash: "admin-test-hash", PlatformAdmin: true})
	if err != nil {
		t.Fatalf("CreateAccount(admin) error = %v", err)
	}
	plan, ok := store.Plan("pro")
	if !ok {
		t.Fatal("Plan(pro) did not find checkout plan")
	}
	plan.Visible = false
	if err := store.UpsertPlanAs(admin.Principal(), plan); err != nil {
		t.Fatalf("hide checkout plan: %v", err)
	}
	pending := core.CheckoutSession{
		ID: "cs_pending", OwnerUserID: principal.UserID, OrganizationID: created.ID,
		PlanID: plan.ID, Interval: IntervalMonthly, Status: "open", ExpiresAt: time.Now().Add(time.Hour),
	}
	if err := store.UpdateAs(principal, func(account *core.Account) error {
		org := account.Organizations[created.ID]
		org.Billing.CustomerID = "cus_test"
		org.Billing.CheckoutID = pending.ID
		org.CheckoutSessions[pending.ID] = pending
		account.Organizations[org.ID] = org
		return nil
	}); err != nil {
		t.Fatalf("prepare pending checkout: %v", err)
	}
	flow := &testBillingFlow{}
	provider := &testProvider{
		organizationID: created.ID, customerID: "cus_test",
		checkoutSession: CheckoutSession{
			ID: pending.ID, URL: "https://checkout.stripe.com/pending", CustomerID: "cus_test",
			ClientReferenceID: created.ID, Status: "open",
		},
	}
	mux := http.NewServeMux()
	mount(mux, core.Deps{Store: store, Sessions: testSessions{principal: principal}}, flow,
		StripeConfig{
			Production: true, Mode: "test", PublicOrigin: "https://dashboard.example",
			PriceIDs: map[PriceKey]string{{PlanKey: PlanPro, Interval: IntervalMonthly}: "price_pro_monthly"},
		}, provider, nil)
	form := url.Values{"plan": {"pro"}, "interval": {"monthly"}, "return": {"/billing"}}
	request := httptest.NewRequest(http.MethodPost, "/billing/checkout", strings.NewReader(form.Encode()))
	request.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	response := httptest.NewRecorder()
	mux.ServeHTTP(response, request)
	if response.Code != http.StatusSeeOther || response.Header().Get("Location") != provider.checkoutSession.URL {
		t.Fatalf("hidden-plan pending checkout response = %d %q", response.Code, response.Header().Get("Location"))
	}
	if provider.checkoutCalls != 0 || flow.externalCheckoutCalls != 0 {
		t.Fatalf("hidden plan issued a replacement checkout: provider creates=%d, local bindings=%d", provider.checkoutCalls, flow.externalCheckoutCalls)
	}
}

func TestStripeCheckoutExpiresProviderSessionWhenLocalBindingFails(t *testing.T) {
	store := core.NewStore()
	account, err := store.CreateAccount(core.User{Email: "owner@example.test", Name: "Owner", PasswordHash: "test-hash", Role: "owner"})
	if err != nil {
		t.Fatalf("CreateAccount() error = %v", err)
	}
	created, err := organization.NewService(store).CreateBusiness(account.Principal(), "Billing Labs")
	if err != nil {
		t.Fatalf("CreateBusiness() error = %v", err)
	}
	account, ok := store.Snapshot(account.ID)
	if !ok {
		t.Fatal("Snapshot() did not find created account")
	}
	principal := account.Principal()
	if err := store.UpdateAs(principal, func(account *core.Account) error {
		org := account.Organizations[created.ID]
		org.Billing.CustomerID = "cus_test"
		account.Organizations[org.ID] = org
		return nil
	}); err != nil {
		t.Fatalf("prepare customer: %v", err)
	}
	flow := &testBillingFlow{recordExternalCheckoutErr: core.ErrConflict}
	provider := &testProvider{organizationID: created.ID, customerID: "cus_test"}
	mux := http.NewServeMux()
	mount(mux, core.Deps{Store: store, Sessions: testSessions{principal: principal}}, flow,
		StripeConfig{
			Production: true, Mode: "test", PublicOrigin: "https://dashboard.example",
			PriceIDs: map[PriceKey]string{{PlanKey: PlanPro, Interval: IntervalMonthly}: "price_pro_monthly"},
		}, provider, nil)
	form := url.Values{"plan": {"pro"}, "interval": {"monthly"}, "return": {"/billing"}}
	request := httptest.NewRequest(http.MethodPost, "/billing/checkout", strings.NewReader(form.Encode()))
	request.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	response := httptest.NewRecorder()
	mux.ServeHTTP(response, request)
	if response.Code != http.StatusConflict || response.Header().Get("Location") != "" {
		t.Fatalf("binding failure response = %d %q", response.Code, response.Header().Get("Location"))
	}
	if provider.checkoutCalls != 1 || provider.expireCheckoutCalls != 1 || provider.expiredCheckoutID != "cs_test" {
		t.Fatalf("provider checkout cleanup = create %d, expire %d (%q)", provider.checkoutCalls, provider.expireCheckoutCalls, provider.expiredCheckoutID)
	}
}

type testBillingFlow struct {
	demoCheckoutCalls         int
	externalCheckoutCalls     int
	events                    []BillingEvent
	recordExternalCheckoutErr error
}

func (flow *testBillingFlow) BeginDemoCheckout(_ core.Principal, _, _, _ string) (core.CheckoutSession, error) {
	flow.demoCheckoutCalls++
	return core.CheckoutSession{ID: "cs_local_test"}, nil
}

func (*testBillingFlow) Checkout(_ core.Principal, sessionID string) (core.CheckoutSession, error) {
	return core.CheckoutSession{ID: sessionID}, nil
}

func (*testBillingFlow) CheckoutForReturn(_ core.Principal, sessionID string) (core.CheckoutSession, error) {
	return core.CheckoutSession{ID: sessionID}, nil
}

func (*testBillingFlow) CompleteDemoCheckout(_ core.Principal, _ string, _ bool) (string, error) {
	return "/billing", nil
}

func (*testBillingFlow) RecordCustomer(_ core.Principal, customerID string) (string, error) {
	return customerID, nil
}

func (flow *testBillingFlow) RecordExternalCheckout(_ core.Principal, _, _, _ string, _ CheckoutSession) error {
	flow.externalCheckoutCalls++
	return flow.recordExternalCheckoutErr
}

func (*testBillingFlow) ExpireExternalCheckout(core.Principal, string) error {
	return nil
}

func (*testBillingFlow) ReconcileCheckoutReturn(_ core.Principal, _ string, _ CheckoutSession) (string, string, error) {
	return "/billing", "pending", nil
}

func (flow *testBillingFlow) ApplyStripeEvent(event BillingEvent) error {
	flow.events = append(flow.events, event)
	return nil
}

type testSessions struct {
	principal core.Principal
}

func (sessions testSessions) Start(http.ResponseWriter, *http.Request, core.Principal) error {
	return nil
}
func (sessions testSessions) Principal(*http.Request) (core.Principal, bool) {
	return sessions.principal, sessions.principal.UserID != ""
}
func (sessions testSessions) BearerPrincipal(*http.Request) (core.Principal, bool) {
	return sessions.Principal(nil)
}
func (testSessions) End(http.ResponseWriter, *http.Request) {}
func (testSessions) RevokeUser(string, string)              {}

type testProvider struct {
	customerCalls       int
	checkoutCalls       int
	expireCheckoutCalls int
	expiredCheckoutID   string
	checkoutSession     CheckoutSession
	portalReturnURL     string
	organizationID      string
	customerID          string
	subscription        SubscriptionSnapshot
	subscriptionErr     error
}

func (provider *testProvider) CreateCustomer(context.Context, string, string, string) (Customer, error) {
	provider.customerCalls++
	if provider.customerID == "" {
		provider.customerID = "cus_test"
	}
	return Customer{ID: provider.customerID}, nil
}
func (provider *testProvider) CreateCheckoutSession(context.Context, CheckoutRequest) (CheckoutSession, error) {
	provider.checkoutCalls++
	return CheckoutSession{ID: "cs_test", URL: "https://checkout.stripe.com/test", CustomerID: provider.customerID, ClientReferenceID: provider.organizationID}, nil
}
func (provider *testProvider) ExpireCheckoutSession(_ context.Context, sessionID string) error {
	provider.expireCheckoutCalls++
	provider.expiredCheckoutID = sessionID
	return nil
}
func (provider *testProvider) RetrieveCheckoutSession(context.Context, string) (CheckoutSession, error) {
	return provider.checkoutSession, nil
}
func (provider *testProvider) RetrieveSubscription(_ context.Context, id string) (SubscriptionSnapshot, error) {
	if provider.subscriptionErr != nil {
		return SubscriptionSnapshot{}, provider.subscriptionErr
	}
	snapshot := provider.subscription
	if snapshot.ID == "" {
		snapshot.ID = id
	}
	if snapshot.CustomerID == "" {
		snapshot.CustomerID = provider.customerID
	}
	if snapshot.Status == "" {
		snapshot.Status = "active"
	}
	if snapshot.PriceID == "" {
		snapshot.PriceID = "price_pro_monthly"
	}
	return snapshot, nil
}
func (provider *testProvider) CreatePortalSession(_ context.Context, _, returnURL, _ string) (PortalSession, error) {
	provider.portalReturnURL = returnURL
	return PortalSession{ID: "bps_test", URL: "https://billing.stripe.com/test"}, nil
}
