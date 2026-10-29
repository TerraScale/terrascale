package tsauth

import (
	"context"
	"errors"
	"net/http"
	"net/url"
	"testing"
	"time"
)

func TestDeviceWaitUsesOriginalExpiryAndCallerCause(t *testing.T) {
	d, _ := newDeviceServer(t, "", func(http.ResponseWriter, url.Values) { t.Error("unexpected token poll") })
	a, err := d.Start(t.Context())
	if err != nil {
		t.Fatal(err)
	}
	if remaining := time.Until(a.expiresAt); remaining <= 0 || remaining > 600*time.Second {
		t.Fatalf("remaining=%s", remaining)
	}
	for _, invalid := range []*DeviceAuthorization{nil, {}, {DeviceCode: "dc", Interval: 1}} {
		if _, err := d.Wait(t.Context(), invalid); err == nil {
			t.Fatal("accepted authorization without Start")
		}
	}
	// A delayed Wait sees the original expiry, irrespective of public ExpiresIn.
	a.expiresAt = time.Now().Add(-time.Second)
	a.ExpiresIn = 1800
	var api *Error
	if _, err := d.Wait(t.Context(), a); !errors.As(err, &api) || api.Code != "expired_token" {
		t.Fatal(err)
	}
	a.expiresAt = time.Now().Add(time.Minute)
	ctx, cancel := context.WithDeadline(t.Context(), time.Now().Add(-time.Second))
	defer cancel()
	if _, err := d.Wait(ctx, a); !errors.Is(err, context.DeadlineExceeded) {
		t.Fatal(err)
	}
	ctx, cancel = context.WithCancel(t.Context())
	cancel()
	if _, err := d.Wait(ctx, a); !errors.Is(err, context.Canceled) {
		t.Fatal(err)
	}
}
