package onboarding

import (
	"context"
	"errors"
	"fmt"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/core/memstore"
	"terrascale.tech/dashboard/internal/features/payments"
	"terrascale.tech/dashboard/internal/features/subscriptions"
	"terrascale.tech/dashboard/internal/platform/csrf"
	renderer "terrascale.tech/dashboard/internal/platform/render"
	"terrascale.tech/dashboard/internal/platform/session"
)

type unavailableCatalogStore struct{ core.Store }

func (unavailableCatalogStore) Plans(context.Context) ([]core.Plan, error) {
	return nil, fmt.Errorf("load plans: %w", core.ErrUnavailable)
}

func TestSaveStepSucceedsDuringCatalogOutage(t *testing.T) {
	for _, tc := range []struct {
		name, current, next string
		input               stepInput
		completed, checkout bool
	}{
		{name: "account information", current: "account-information", next: "payment-method", input: stepInput{Step: "account-information", DisplayName: "New Name"}},
		{name: "free payment", current: "payment-method", next: "project", input: stepInput{Step: "payment-method", Bypass: true}},
		{name: "paid checkout", current: "payment-method", next: "payment-method", input: stepInput{Step: "payment-method", Plan: "starter", Interval: "monthly"}, checkout: true},
		{name: "project", current: "project", input: stepInput{Step: "project", Name: "First project", Region: "FRA"}, completed: true},
	} {
		t.Run(tc.name, func(t *testing.T) {
			store := memstore.New()
			account, err := store.CreateAccount(t.Context(), core.User{Name: "Owner", Email: "onboarding@example.test", PasswordHash: "unused"})
			if err != nil {
				t.Fatal(err)
			}
			p := account.Principal()
			if err := store.UpdateAs(t.Context(), p, func(account *core.Account) error {
				account.OnboardingStep = tc.current
				return nil
			}); err != nil {
				t.Fatal(err)
			}
			deps := core.Deps{Store: unavailableCatalogStore{store}, Demo: true}
			service := Service{deps: deps, billing: payments.NewService(deps, subscriptions.NewBillingService(deps.Store), nil)}
			result, err := service.SaveStep(t.Context(), p, tc.input)
			if err != nil || result.Completed != tc.completed || result.Step != tc.next {
				t.Fatalf("SaveStep = %+v, %v, want completed=%t, step=%q", result, err, tc.completed, tc.next)
			}
			updated, err := store.SnapshotAs(t.Context(), p)
			if err != nil {
				t.Fatal(err)
			}
			if updated.OnboardingStep != tc.next || updated.OnboardingCompleted != tc.completed {
				t.Fatalf("saved progress = %q, completed=%t", updated.OnboardingStep, updated.OnboardingCompleted)
			}
			if tc.input.DisplayName != "" && updated.User.Name != tc.input.DisplayName {
				t.Fatalf("saved name = %q, want %q", updated.User.Name, tc.input.DisplayName)
			}
			organization := updated.Organizations[p.OrganizationID]
			if tc.completed {
				project := organization.Projects[p.ProjectID]
				if project.Name != tc.input.Name || project.Region != tc.input.Region {
					t.Fatalf("saved project = %q in %q", project.Name, project.Region)
				}
			}
			if tc.checkout {
				checkout := organization.CheckoutSessions[organization.Billing.CheckoutID]
				if checkout.ID == "" || result.CheckoutURL != "/checkout/"+checkout.ID || checkout.ReturnPath != "/onboarding" {
					t.Fatalf("checkout result = %+v, saved checkout = %+v", result, checkout)
				}
			} else if result.CheckoutURL != "" {
				t.Fatalf("CheckoutURL = %q, want empty", result.CheckoutURL)
			}
			if _, err := service.Status(t.Context(), p); !errors.Is(err, core.ErrUnavailable) {
				t.Fatalf("Status = %v, want catalog outage", err)
			}
		})
	}
}

type failingSnapshotStore struct {
	core.Store
	calls, failAt int
	err           error
}

func (store *failingSnapshotStore) SnapshotAs(ctx context.Context, p core.Principal) (core.Account, error) {
	store.calls++
	if store.calls == store.failAt {
		return core.Account{}, store.err
	}
	return store.Store.SnapshotAs(ctx, p)
}

func TestOnboardingPageReportsSnapshotOutageAsUnavailable(t *testing.T) {
	for _, tc := range []struct {
		name   string
		failAt int
		err    error
		want   int
	}{
		{name: "initial outage", failAt: 1, err: fmt.Errorf("read account: %w", core.ErrUnavailable), want: http.StatusServiceUnavailable},
		{name: "confirmed payment reread outage", failAt: 2, err: fmt.Errorf("reread account: %w", core.ErrUnavailable), want: http.StatusServiceUnavailable},
		{name: "missing account", failAt: 1, err: core.ErrNotFound, want: http.StatusOK},
		{name: "missing account after payment", failAt: 2, err: core.ErrNotFound, want: http.StatusUnauthorized},
	} {
		t.Run(tc.name, func(t *testing.T) {
			store := memstore.New()
			account, err := store.CreateAccount(t.Context(), core.User{Name: "Owner", Email: "onboarding@example.test", PasswordHash: "unused"})
			if err != nil {
				t.Fatal(err)
			}
			p := account.Principal()
			path := "/onboarding"
			if tc.failAt == 2 {
				billing := subscriptions.NewBillingService(store)
				checkout, err := billing.BeginDemoCheckout(t.Context(), p, "starter", "monthly", "/onboarding")
				if err != nil {
					t.Fatal(err)
				}
				path, err = billing.CompleteDemoCheckout(t.Context(), p, checkout.ID, false)
				if err != nil {
					t.Fatal(err)
				}
			}
			failing := &failingSnapshotStore{Store: store, failAt: tc.failAt, err: tc.err}
			sessions := session.New(failing, session.Options{})
			guard := csrf.New(sessions, csrf.Options{})
			mux := http.NewServeMux()
			Mount(mux, core.Deps{Store: failing, Sessions: sessions, CSRF: guard, Render: renderer.New(failing, guard, nil, false), Tools: core.NewTools(failing), Demo: true}, nil)
			signIn := httptest.NewRecorder()
			if err := sessions.Start(signIn, httptest.NewRequest(http.MethodGet, "/", nil), p); err != nil {
				t.Fatal(err)
			}
			request := httptest.NewRequest(http.MethodGet, path, nil)
			for _, cookie := range signIn.Result().Cookies() {
				request.AddCookie(cookie)
			}
			response := httptest.NewRecorder()
			mux.ServeHTTP(response, request)
			if response.Code != tc.want {
				t.Fatalf("GET %s = %d %q, want %d", path, response.Code, response.Body.String(), tc.want)
			}
			if tc.want == http.StatusServiceUnavailable && !strings.Contains(response.Body.String(), "service unavailable") {
				t.Fatalf("outage response = %q", response.Body.String())
			}
			if tc.failAt == 2 {
				updated, err := store.SnapshotAs(t.Context(), p)
				if err != nil || updated.OnboardingStep != "project" {
					t.Fatalf("confirmed payment progress = %q, %v, want project", updated.OnboardingStep, err)
				}
			}
		})
	}
}
