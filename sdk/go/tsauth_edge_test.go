package sdk

// Edge-case coverage for the ts-auth OIDC client's request pipeline, one
// section per matrix block: B status/error mapping, D retry and cancellation,
// G configuration. Cases that pin debatable behavior are marked "pins current
// behavior".

import (
	"bytes"

	"io"
	"net/http"

	"strings"
)

// tsAuthEdgeRecorder captures the exact wire form of every request.
type tsAuthEdgeRecorder struct {
	requests []string
	bodies   [][]byte
}

func (rec *tsAuthEdgeRecorder) record(r *http.Request) {
	body, _ := io.ReadAll(r.Body)
	rec.requests = append(rec.requests, r.Method+" "+r.URL.RequestURI())
	rec.bodies = append(rec.bodies, body)
	r.Body = io.NopCloser(bytes.NewReader(body))
}

func (rec *tsAuthEdgeRecorder) count(prefix string) int {
	total := 0
	for _, request := range rec.requests {
		if strings.HasPrefix(request, prefix) {
			total++
		}
	}
	return total
}

// newTSAuthEdgeInstance serves handle behind the recorder and returns an
// instance client built from cfg.

type tsAuthEdgeResponse struct {
	status      int
	body        string
	contentType string // default: application/json when body is non-empty
	retryAfter  string
	wwwAuth     string
}

func (resp tsAuthEdgeResponse) write(w http.ResponseWriter) {
	if resp.body != "" || resp.contentType != "" {
		contentType := resp.contentType
		if contentType == "" {
			contentType = "application/json"
		}
		w.Header().Set("Content-Type", contentType)
	}
	if resp.retryAfter != "" {
		w.Header().Set("Retry-After", resp.retryAfter)
	}
	if resp.wwwAuth != "" {
		w.Header().Set("WWW-Authenticate", resp.wwwAuth)
	}
	w.WriteHeader(resp.status)
	_, _ = io.WriteString(w, resp.body)
}

// Section A: response-decode edges.

// Section C: request-construction edges (wire encoding).

// Section C: preflight validation (no HTTP issued).

// Section D: retry, deadline, and cancellation edges.

type tsAuthEdgeTransport func(*http.Request) (*http.Response, error)

func (f tsAuthEdgeTransport) RoundTrip(r *http.Request) (*http.Response, error) { return f(r) }

// D35: safe reads stop after the fixed retry budget.

// D36: Retry-After: 0 retries immediately, with no backoff sleep (the
// fixed base delay is bypassed).

// D37: POST + 503 is never retried.

// D38: 400/409/401/403 are never retried, even for GET.

// D39: transport errors on GET retry when enabled; POST never retries.

// D40: cancellation before the call surfaces a cancelled error with no
// HTTP issued.

// D41: cancellation during the delayed retry sleep is honored. The
// handler cancels deterministically after answering once, so the cancel
// always lands before a second request regardless of scheduling.

// Section E: pagination edges.

// Section F: webhook verifier edges.

// Section G: configuration edges.

// G56: a base URL with a query, fragment, or userinfo is a construction
// error; a path and an empty base (the default origin) are ALLOWED at
// construction (the product client defaults to the product origin).

// G56b: ts-auth defaults to the global auth origin; TSAuthBaseURL overrides it.

// G57: plain HTTP is allowed only for loopback (always) and .test hosts
// (only with an explicit HTTPClient); a production http host is rejected
// even with an explicit transport.

// G58: the base URL with and without a trailing slash behaves
// identically.

// G60: mutating the Config record after construction does not affect the
// client (NewClient snapshots it).

// G61: TSAuthBaseURL routes ts-auth traffic to the ts-auth origin, never the
// product origin.

// B23: an oversized body keeps its observed status as a non-retryable
// response_too_large APIError.
