package v1

import (
	"context"
	"errors"
	"io"
	"net/http"
	"strings"
	"testing"
	"time"
)

// SetResponseLimitForTest permits tiny boundary fixtures without a production knob.
func SetResponseLimitForTest(c *Client, bytes int64) { c.maxResponse = bytes }

type boundedTransport func(*http.Request) (*http.Response, error)

func (f boundedTransport) RoundTrip(r *http.Request) (*http.Response, error) { return f(r) }

func TestServerCooldownIsNeverShortened(t *testing.T) {
	for _, shape := range []bool{false, true} {
		calls := 0
		c, err := New(Config{Token: "fixture.jwt.sig", HTTPClient: &http.Client{Transport: boundedTransport(func(r *http.Request) (*http.Response, error) {
			calls++
			if _, ok := r.Context().Deadline(); !ok {
				t.Error("operation has no owned deadline")
			}
			return &http.Response{StatusCode: 429, Header: http.Header{"Retry-After": {"120"}}, Body: io.NopCloser(strings.NewReader(`{"error":{"code":"rate_limited"}}`))}, nil
		})}})
		if err != nil {
			t.Fatal(err)
		}
		if shape {
			_, _, err = c.shapeGET(t.Context(), []string{"shape"}, nil, nil)
		} else {
			_, err = c.do(t.Context(), request{method: http.MethodGet, replaySafe: true})
		}
		var refused *RetryBudgetError
		if !errors.As(err, &refused) || refused.RetryAfter != 120*time.Second || !errors.Is(err, ErrRateLimited) || calls != 1 {
			t.Fatalf("shape=%v error=%v calls=%d", shape, err, calls)
		}
	}
	for _, value := range []string{"120", "9999999999999999999999999999999999"} {
		duration, ok := parseRetryAfter(http.Header{"Retry-After": {value}})
		if !ok || duration < 120*time.Second {
			t.Fatalf("cooldown=%s valid=%v", duration, ok)
		}
	}
	ctx, cancel := context.WithCancel(t.Context())
	cancel()
	if err := waitRetry(ctx, time.Second, &APIError{}); !errors.Is(err, context.Canceled) {
		t.Fatal(err)
	}
}

func TestShapeOverloadRetriesHaveFiniteAttemptBudget(t *testing.T) {
	calls := 0
	c, err := New(Config{Token: "fixture.jwt.sig", HTTPClient: &http.Client{Transport: boundedTransport(func(*http.Request) (*http.Response, error) {
		calls++
		return &http.Response{StatusCode: 429, Header: http.Header{"Retry-After": {"0"}}, Body: io.NopCloser(strings.NewReader(`{"error":{"code":"shape_busy"}}`))}, nil
	})}})
	if err != nil {
		t.Fatal(err)
	}
	if _, _, err := c.shapeGET(t.Context(), []string{"shape"}, nil, nil); !errors.Is(err, ErrShapeBusy) || calls != DefaultMaxRetries+1 {
		t.Fatalf("error=%v calls=%d", err, calls)
	}
}

func TestQuotaIsNotRetriedAndEarlierCallerDeadlineWins(t *testing.T) {
	if _, retry := retryDelay(&APIError{Status: 429, Code: ErrQuotaExceeded}, 0, true, true); retry {
		t.Fatal("quota failure retried")
	}
	c, err := New(Config{Token: "fixture.jwt.sig", HTTPClient: &http.Client{Transport: boundedTransport(func(r *http.Request) (*http.Response, error) { <-r.Context().Done(); return nil, r.Context().Err() })}})
	if err != nil {
		t.Fatal(err)
	}
	ctx, cancel := context.WithTimeout(t.Context(), time.Millisecond)
	defer cancel()
	if _, err := c.do(ctx, request{method: http.MethodGet, replaySafe: true}); !errors.Is(err, context.DeadlineExceeded) {
		t.Fatal(err)
	}
}
