package v1

import (
	"context"
	"errors"

	"net/http"

	"testing"
	"time"
)

// SetResponseLimitForTest permits tiny boundary fixtures without a production knob.
func SetResponseLimitForTest(c *Client, bytes int64) { c.maxResponse = bytes }

type boundedTransport func(*http.Request) (*http.Response, error)

func (f boundedTransport) RoundTrip(r *http.Request) (*http.Response, error) { return f(r) }

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
