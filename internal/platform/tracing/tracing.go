// Package tracing carries a W3C trace ID through a request: the middleware
// continues an incoming traceparent or starts one, and the transport passes
// it on to the services the dashboard calls.
package tracing

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"net/http"
	"strings"
)

type contextKey struct{}

// Middleware continues the request's trace or starts a new one, keeps its
// trace ID in the request context and echoes it in the traceparent header.
func Middleware(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		traceID, ok := parseTraceparent(r.Header.Get("traceparent"))
		if !ok {
			traceID = randomHex(16)
		}
		w.Header().Set("traceparent", traceparent(traceID))
		next.ServeHTTP(w, r.WithContext(context.WithValue(r.Context(), contextKey{}, traceID)))
	})
}

// TraceID returns the trace ID of the request ctx belongs to, if any.
func TraceID(ctx context.Context) (string, bool) {
	traceID, ok := ctx.Value(contextKey{}).(string)
	return traceID, ok
}

// Transport sends the request's trace ID to the service next reaches. Calls
// made outside a request carry no trace and are sent unchanged.
func Transport(next http.RoundTripper) http.RoundTripper {
	return transport{next}
}

type transport struct{ next http.RoundTripper }

func (t transport) RoundTrip(r *http.Request) (*http.Response, error) {
	traceID, ok := TraceID(r.Context())
	if !ok {
		return t.next.RoundTrip(r)
	}
	// RoundTrip must not modify the caller's request.
	clone := r.Clone(r.Context())
	clone.Header.Set("traceparent", traceparent(traceID))
	return t.next.RoundTrip(clone)
}

// parseTraceparent accepts only version 00 with a non-zero, lowercase trace
// ID and parent ID, as the W3C recommendation requires.
func parseTraceparent(header string) (string, bool) {
	parts := strings.Split(header, "-")
	if len(parts) != 4 || parts[0] != "00" || !lowerHex(parts[1], 32) || !lowerHex(parts[2], 16) || !lowerHex(parts[3], 2) {
		return "", false
	}
	if strings.Trim(parts[1], "0") == "" || strings.Trim(parts[2], "0") == "" {
		return "", false
	}
	return parts[1], true
}

func lowerHex(s string, n int) bool {
	if len(s) != n {
		return false
	}
	for _, c := range s {
		if (c < '0' || c > '9') && (c < 'a' || c > 'f') {
			return false
		}
	}
	return true
}

// traceparent names traceID with a new span ID, sampled.
func traceparent(traceID string) string {
	return "00-" + traceID + "-" + randomHex(8) + "-01"
}

func randomHex(n int) string {
	b := make([]byte, n)
	// Since Go 1.24 rand.Read never returns an error.
	_, _ = rand.Read(b)
	return hex.EncodeToString(b)
}
