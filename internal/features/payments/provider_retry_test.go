package payments

import (
	"context"
	"errors"
	"fmt"
	"net/http"
	"net/http/httptest"
	"sync/atomic"
	"testing"
	"time"

	stripe "github.com/stripe/stripe-go/v86"
	"terrascale.tech/dashboard/internal/core"
)

func TestStripeProviderCreateCustomerRetries(t *testing.T) {
	for _, tc := range []struct {
		name      string
		errorType string
		code      stripe.ErrorCode
		failures  int64
		wantCalls int64
		wantError bool
	}{
		{"lock timeout then success", "rate_limit_error", stripe.ErrorCodeLockTimeout, 1, 2, false},
		{"ordinary rate limit", "rate_limit_error", stripe.ErrorCodeRateLimit, 1, 1, true},
		{"SDK rate limit type", "rate_limit", stripe.ErrorCodeRateLimit, 1, 1, true},
		{"lock timeout exhausted", "rate_limit_error", stripe.ErrorCodeLockTimeout, stripeMaxNetworkRetries + 1, stripeMaxNetworkRetries + 1, true},
	} {
		t.Run(tc.name, func(t *testing.T) {
			request := CustomerRequest{
				Email:          "retry@example.test",
				OrganizationID: core.NewID(),
				IdempotencyKey: core.NewID(),
			}
			const customerID = "cus_retry_test"
			var calls atomic.Int64
			server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				attempt := calls.Add(1)
				if r.Method != http.MethodPost || r.URL.Path != "/v1/customers" {
					t.Errorf("unexpected request: %s %s", r.Method, r.URL.Path)
				}
				if got := r.Header.Get("Idempotency-Key"); got != request.IdempotencyKey {
					t.Errorf("attempt %d idempotency key = %q, want %q", attempt, got, request.IdempotencyKey)
				}
				if err := r.ParseForm(); err != nil {
					t.Errorf("parse request form: %v", err)
				}
				if got := r.PostForm.Get("metadata[organization_id]"); got != request.OrganizationID {
					t.Errorf("organization metadata = %q, want %q", got, request.OrganizationID)
				}
				w.Header().Set("Content-Type", "application/json")
				if attempt <= tc.failures {
					w.WriteHeader(http.StatusTooManyRequests)
					fmt.Fprintf(w, `{"error":{"type":%q,"code":%q,"message":"synthetic retry test"}}`, tc.errorType, tc.code)
					return
				}
				fmt.Fprintf(w, `{"id":%q,"object":"customer"}`, customerID)
			}))
			defer server.Close()

			backends := stripe.NewBackendsWithConfig(&stripe.BackendConfig{
				HTTPClient:        server.Client(),
				URL:               stripe.String(server.URL),
				MaxNetworkRetries: stripe.Int64(stripeMaxNetworkRetries),
				LeveledLogger:     &stripe.LeveledLogger{Level: stripe.LevelNull},
			})
			provider := &stripeProvider{client: stripe.NewClient("sk_test_synthetic", stripe.WithBackends(backends))}
			ctx, cancel := context.WithTimeout(t.Context(), 15*time.Second)
			defer cancel()
			customer, err := provider.CreateCustomer(ctx, request)
			if got := calls.Load(); got != tc.wantCalls {
				t.Errorf("server calls = %d, want %d", got, tc.wantCalls)
			}
			if tc.wantError {
				var stripeErr *stripe.Error
				if !errors.As(err, &stripeErr) || stripeErr.HTTPStatusCode != http.StatusTooManyRequests || stripeErr.Code != tc.code {
					t.Fatalf("error = %v, want Stripe HTTP 429 with code %q", err, tc.code)
				}
				return
			}
			if err != nil {
				t.Fatalf("CreateCustomer: %v", err)
			}
			if customer.ID != customerID {
				t.Errorf("customer ID = %q, want %q", customer.ID, customerID)
			}
		})
	}
}
