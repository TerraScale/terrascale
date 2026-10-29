package sdk

import (
	"context"
	"errors"
	"io"
	"net/http"
	"strings"
	"testing"
	"time"
)

func TestRootRetriesNeverShortenServerCooldownOrReplayQuota(t *testing.T) {
	for _, code := range []string{"rate_limited", "quota_exceeded"} {
		calls := 0
		client, err := NewClient(Config{TSAuthBaseURL: "https://auth.example", HTTPClient: &http.Client{Transport: pinningRoundTripperFunc(func(r *http.Request) (*http.Response, error) {
			calls++
			deadline, ok := r.Context().Deadline()
			if !ok || time.Until(deadline) > MaxRequestTimeout {
				t.Error("missing fixed operation deadline")
			}
			return &http.Response{StatusCode: 429, Header: http.Header{"Retry-After": {"120"}}, Body: io.NopCloser(strings.NewReader(`{"error":{"code":"` + code + `"}}`))}, nil
		})}})
		if err != nil {
			t.Fatal(err)
		}
		instance, err := client.TSAuth.Instance("acme")
		if err != nil {
			t.Fatal(err)
		}
		_, err = instance.OIDC.GetUserInfo(t.Context(), "fixture")
		var refused *RetryBudgetError
		if code == "rate_limited" && (!errors.As(err, &refused) || refused.RetryAfter != 120*time.Second) {
			t.Fatal(err)
		}
		var api *APIError
		if !errors.As(err, &api) || api.Code != code || calls != 1 {
			t.Fatalf("error=%v calls=%d", err, calls)
		}
	}
	ctx, cancel := context.WithCancel(t.Context())
	cancel()
	if err := waitRootRetry(ctx, time.Second, &APIError{}); !errors.Is(err, context.Canceled) {
		t.Fatal(err)
	}
}
