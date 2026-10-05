package subscriptions

import (
	"context"
	"fmt"
	"net/http"
	"net/http/httptest"
	"testing"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/core/memstore"
)

// planOutageStore fails plan lookups as a database outage would.
type planOutageStore struct{ core.Store }

func (planOutageStore) Plan(context.Context, string) (core.Plan, error) {
	return core.Plan{}, fmt.Errorf("plan: %w", core.ErrUnavailable)
}

func TestCheckoutPageReportsStoreOutageAsUnavailable(t *testing.T) {
	store := memstore.New()
	account, err := store.CreateAccount(t.Context(), core.User{Name: "Owner", Email: "checkout-outage@example.test", PasswordHash: "unused"})
	if err != nil {
		t.Fatal(err)
	}
	principal := account.Principal()
	session, err := NewBillingService(store).BeginDemoCheckout(t.Context(), principal, "starter", "monthly", "/billing")
	if err != nil {
		t.Fatal(err)
	}
	mux := http.NewServeMux()
	Mount(mux, core.Deps{Store: planOutageStore{store}, Tools: core.NewTools(store), Sessions: planChangeSessions{principal: principal}, Demo: true}, nil)
	response := httptest.NewRecorder()
	mux.ServeHTTP(response, httptest.NewRequest(http.MethodGet, "/checkout/"+session.ID, nil))
	if response.Code != http.StatusServiceUnavailable {
		t.Fatalf("checkout page during a store outage = %d, want 503", response.Code)
	}
}
