package payments_test

import (
	"context"
	"net/http"
	"net/http/httptest"
	"net/url"
	"strings"
	"testing"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/features/payments"
	"terrascale.tech/dashboard/internal/features/subscriptions"
)

// checkoutProvider creates the customer and Checkout Session a first live
// checkout asks Stripe for.
type checkoutProvider struct {
	payments.BillingProvider
	sessions int
}

func (*checkoutProvider) CreateCustomer(context.Context, payments.CustomerRequest) (payments.Customer, error) {
	return payments.Customer{ID: "cus_checkout"}, nil
}

func (provider *checkoutProvider) CreateCheckoutSession(_ context.Context, request payments.CheckoutRequest) (payments.CheckoutSession, error) {
	provider.sessions++
	return payments.CheckoutSession{
		ID: "cs_test_checkout", URL: "https://checkout.stripe.test/c/pay/cs_test_checkout",
		CustomerID: request.CustomerID, ClientReferenceID: request.OrganizationID, Status: "open", ExpiresAt: request.ExpiresAt,
	}, nil
}

func TestLiveCheckoutGatesOnlyPaidPlansWithoutSubscription(t *testing.T) {
	for _, tc := range []struct {
		name   string
		planID string
		status int
	}{
		{name: "free personal organization", planID: "plan_free", status: http.StatusSeeOther},
		{name: "paid plan outside Stripe", planID: "plan_pro", status: http.StatusConflict},
	} {
		t.Run(tc.name, func(t *testing.T) {
			store := core.NewStore()
			account, err := store.CreateAccount(core.User{Name: "Owner", Email: "checkout-owner@example.test", PasswordHash: "unused"})
			if err != nil {
				t.Fatal(err)
			}
			principal := account.Principal()
			if err := store.UpdateAs(principal, func(account *core.Account) error {
				org := account.Organizations[principal.OrganizationID]
				org.PlanID = tc.planID
				account.Organizations[org.ID] = org
				return nil
			}); err != nil {
				t.Fatal(err)
			}
			provider := &checkoutProvider{}
			mux := http.NewServeMux()
			payments.Mount(mux, core.Deps{Store: store, Sessions: fixtureSessions{principal: principal}},
				subscriptions.NewBillingService(store), payments.NewStripeForTest(testConfig, provider, payments.NewMemoryStateStore()))

			form := url.Values{"plan": {payments.PlanStarter}, "interval": {payments.IntervalMonthly}, "return": {"/onboarding"}}
			request := httptest.NewRequest(http.MethodPost, "/billing/checkout", strings.NewReader(form.Encode()))
			request.Header.Set("Content-Type", "application/x-www-form-urlencoded")
			response := httptest.NewRecorder()
			mux.ServeHTTP(response, request)
			if response.Code != tc.status {
				t.Fatalf("checkout = %d %q, want %d", response.Code, response.Body.String(), tc.status)
			}

			wantSessions, wantCheckoutID := 0, ""
			if tc.status == http.StatusSeeOther {
				wantSessions, wantCheckoutID = 1, "cs_test_checkout"
				if location := response.Header().Get("Location"); !strings.HasPrefix(location, "https://checkout.stripe.test/") {
					t.Fatalf("checkout redirected to %q, want the provider session", location)
				}
			}
			snapshot, err := store.SnapshotAs(principal)
			if err != nil {
				t.Fatal(err)
			}
			if checkoutID := snapshot.Organizations[principal.OrganizationID].Billing.CheckoutID; provider.sessions != wantSessions || checkoutID != wantCheckoutID {
				t.Fatalf("provider sessions = %d, recorded checkout = %q, want %d and %q", provider.sessions, checkoutID, wantSessions, wantCheckoutID)
			}
		})
	}
}
