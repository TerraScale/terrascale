package sdk

// Edge-case coverage for the ts-auth OIDC client's request pipeline, one
// section per matrix block: B status/error mapping, D retry and cancellation,
// G configuration. Cases that pin debatable behavior are marked "pins current
// behavior".

import (
	"bytes"
	"context"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/http/httptest"
	"reflect"
	"strconv"
	"strings"
	"sync/atomic"
	"testing"
	"time"
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

func newTSAuthEdgeInstanceFor(t *testing.T, baseURL string, cfg Config) *TSAuthInstanceClient {
	t.Helper()
	cfg.TSAuthBaseURL = baseURL
	client, err := NewClient(cfg)
	if err != nil {
		t.Fatal(err)
	}
	instance, err := client.TSAuth.Instance("acme")
	if err != nil {
		t.Fatal(err)
	}
	return instance
}

// newTSAuthEdgeInstance serves handle behind the recorder and returns an
// instance client built from cfg.
func newTSAuthEdgeInstance(t *testing.T, cfg Config, handle func(w http.ResponseWriter, r *http.Request, body []byte)) (*TSAuthInstanceClient, *tsAuthEdgeRecorder) {
	t.Helper()
	rec := &tsAuthEdgeRecorder{}
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		rec.record(r)
		handle(w, r, rec.bodies[len(rec.bodies)-1])
	}))
	t.Cleanup(server.Close)
	return newTSAuthEdgeInstanceFor(t, server.URL, cfg), rec
}

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

func tsAuthEdgeJSON(w http.ResponseWriter, body string) {
	tsAuthEdgeResponse{status: http.StatusOK, body: body}.write(w)
}

func tsAuthEdgeRequireAPIError(t *testing.T, err error, route string, status int, code, message string) *APIError {
	t.Helper()
	var apiErr *APIError
	if !errors.As(err, &apiErr) {
		t.Fatalf("error = %v (%T), want *APIError", err, err)
	}
	if apiErr.Route != route || apiErr.Status != status || apiErr.Code != code || apiErr.Message != message {
		t.Fatalf("api error = %+v, want route %q status %d code %q message %q", apiErr, route, status, code, message)
	}
	return apiErr
}

// Section A: response-decode edges.

// Section C: request-construction edges (wire encoding).

// Section C: preflight validation (no HTTP issued).

// Section D: retry, deadline, and cancellation edges.

type tsAuthEdgeTransport func(*http.Request) (*http.Response, error)

func (f tsAuthEdgeTransport) RoundTrip(r *http.Request) (*http.Response, error) { return f(r) }

func TestTSAuthEdgeRetryAndCancellation(t *testing.T) {
	ctx := t.Context()
	edgeClient := TSAuthOAuthClientAuthentication{ClientID: "cli_1", ClientSecret: "csek", Method: TSAuthClientSecretPost}

	// D35: safe reads stop after the fixed retry budget.
	instance, rec := newTSAuthEdgeInstance(t, Config{}, func(w http.ResponseWriter, r *http.Request, _ []byte) {
		tsAuthEdgeResponse{status: http.StatusServiceUnavailable}.write(w)
	})
	_, err := instance.OIDC.GetUserInfo(ctx, "access-token")
	tsAuthEdgeRequireAPIError(t, err, "tsauth.oidc.userinfo", http.StatusServiceUnavailable, "server_error", "Service Unavailable")
	if got := rec.count("GET /acme/oauth/userinfo"); got != 4 {
		t.Fatalf("503 attempts = %d, want exactly 4", got)
	}

	// D36: Retry-After: 0 retries immediately, with no backoff sleep (the
	// fixed base delay is bypassed).
	var retriedAt time.Time
	instance, rec = newTSAuthEdgeInstance(t, Config{}, func(w http.ResponseWriter, r *http.Request, _ []byte) {
		if rec.count("GET /acme/oauth/userinfo") == 1 {
			tsAuthEdgeResponse{status: http.StatusServiceUnavailable, retryAfter: "0"}.write(w)
			return
		}
		retriedAt = time.Now()
		tsAuthEdgeJSON(w, `{"sub":"01ARZ3NDEKTSV4RRFFQ69G5FA4"}`)
	})
	start := time.Now()
	if _, err := instance.OIDC.GetUserInfo(ctx, "access-token"); err != nil {
		t.Fatal(err)
	}
	if elapsed := retriedAt.Sub(start); elapsed > time.Second {
		t.Fatalf("Retry-After: 0 retried after %s, want no sleep", elapsed)
	}

	// D37: POST + 503 is never retried.
	instance, rec = newTSAuthEdgeInstance(t, Config{}, func(w http.ResponseWriter, r *http.Request, _ []byte) {
		tsAuthEdgeResponse{status: http.StatusServiceUnavailable, body: `{"error":{"code":"server_error","message":"unavailable"}}`}.write(w)
	})
	err = instance.OIDC.RevokeToken(ctx, edgeClient, "token")
	tsAuthEdgeRequireAPIError(t, err, "tsauth.oidc.token.revoke", http.StatusServiceUnavailable, "server_error", "unavailable")
	if got := rec.count("POST /acme/oauth/revoke"); got != 1 {
		t.Fatalf("post 503 attempts = %d, want exactly 1", got)
	}

	// D38: 400/409/401/403 are never retried, even for GET.
	responses := map[string]int{
		"u400": http.StatusBadRequest,
		"u409": http.StatusConflict,
		"u401": http.StatusUnauthorized,
		"u403": http.StatusForbidden,
	}
	hits := map[string]int{}
	instance, _ = newTSAuthEdgeInstance(t, Config{}, func(w http.ResponseWriter, r *http.Request, _ []byte) {
		token := strings.TrimPrefix(r.Header.Get("Authorization"), "Bearer ")
		status, ok := responses[token]
		if !ok {
			t.Errorf("unexpected token %q", token)
			http.NotFound(w, r)
			return
		}
		hits[token]++
		tsAuthEdgeResponse{status: status, body: `{"error":{"code":"nope","message":"nope"}}`}.write(w)
	})
	for token, status := range responses {
		_, err := instance.OIDC.GetUserInfo(ctx, token)
		tsAuthEdgeRequireAPIError(t, err, "tsauth.oidc.userinfo", status, "nope", "nope")
		if hits[token] != 1 {
			t.Fatalf("%s attempts = %d, want exactly 1", token, hits[token])
		}
	}

	// D39: transport errors on GET retry when enabled; POST never retries.
	transportCalls := 0
	failing := tsAuthEdgeTransport(func(*http.Request) (*http.Response, error) {
		transportCalls++
		return nil, errors.New("connection reset by peer")
	})
	instance, _ = newTSAuthEdgeInstance(t, Config{
		HTTPClient: &http.Client{Transport: failing},
	}, func(w http.ResponseWriter, r *http.Request, _ []byte) { http.NotFound(w, r) })
	_, err = instance.OIDC.GetUserInfo(ctx, "access-token")
	if err == nil || !strings.Contains(err.Error(), "tsauth.oidc.userinfo") || !strings.Contains(err.Error(), "connection reset by peer") {
		t.Fatalf("GET transport error = %v", err)
	}
	var apiErr *APIError
	if errors.As(err, &apiErr) {
		t.Fatalf("GET transport error = %v, want a wrapped transport error", err)
	}
	if transportCalls != 4 {
		t.Fatalf("GET transport attempts = %d, want 4 (retried)", transportCalls)
	}
	transportCalls = 0
	err = instance.OIDC.RevokeToken(ctx, edgeClient, "token")
	if err == nil || !strings.Contains(err.Error(), "connection reset by peer") {
		t.Fatalf("POST transport error = %v", err)
	}
	if transportCalls != 1 {
		t.Fatalf("POST transport attempts = %d, want exactly 1 (never retried)", transportCalls)
	}

	// D40: cancellation before the call surfaces a cancelled error with no
	// HTTP issued.
	instance, rec = newTSAuthEdgeInstance(t, Config{}, func(w http.ResponseWriter, r *http.Request, _ []byte) {
		t.Errorf("unexpected request %s %s", r.Method, r.URL.RequestURI())
		http.NotFound(w, r)
	})
	cancelledCtx, cancel := context.WithCancel(ctx)
	cancel()
	_, err = instance.OIDC.GetUserInfo(cancelledCtx, "access-token")
	if !errors.Is(err, context.Canceled) {
		t.Fatalf("pre-cancelled error = %v, want context.Canceled", err)
	}
	if len(rec.requests) != 0 {
		t.Fatalf("pre-cancelled call issued requests %#v", rec.requests)
	}

	// D41: cancellation during the delayed retry sleep is honored. The
	// handler cancels deterministically after answering once, so the cancel
	// always lands before a second request regardless of scheduling.
	sleepCtx, cancelSleep := context.WithCancel(ctx)
	var answered atomic.Bool
	instance, rec = newTSAuthEdgeInstance(t, Config{}, func(w http.ResponseWriter, r *http.Request, _ []byte) {
		tsAuthEdgeResponse{status: http.StatusTooManyRequests, body: `{"error":{"code":"rate_limited","message":"slow down"}}`, retryAfter: "30"}.write(w)
		if answered.CompareAndSwap(false, true) {
			cancelSleep()
		}
	})
	start = time.Now()
	_, err = instance.OIDC.GetUserInfo(sleepCtx, "access-token")
	if !errors.Is(err, context.Canceled) {
		t.Fatalf("sleep cancellation error = %v (%T), want context.Canceled", err, err)
	}
	if got := rec.count("GET /acme/oauth/userinfo"); got != 1 {
		t.Fatalf("attempts before cancel = %d, want 1", got)
	}
	if elapsed := time.Since(start); elapsed > 5*time.Second {
		t.Fatalf("cancellation took %s, want it honored during the sleep", elapsed)
	}
}

// Section E: pagination edges.

// Section F: webhook verifier edges.

// Section G: configuration edges.

func TestTSAuthEdgeConfig(t *testing.T) {
	// G56: a base URL with a query, fragment, or userinfo is a construction
	// error; a path and an empty base (the default origin) are ALLOWED at
	// construction (the product client defaults to the product origin).
	for _, base := range []string{
		"https://api.example.com/?q=1",
		"https://api.example.com/#fragment",
		"https://user:pass@api.example.com/",
	} {
		if _, err := NewClient(Config{APIBaseURL: base}); err == nil {
			t.Fatalf("base URL %q accepted", base)
		}
	}
	if _, err := NewClient(Config{APIBaseURL: "http://127.0.0.1:1/prefix"}); err != nil {
		t.Fatalf("base URL with path rejected: %v", err)
	}
	defaultClient, err := NewClient(Config{})
	if err != nil {
		t.Fatalf("empty base URL rejected: %v", err)
	}

	// G56b: ts-auth defaults to the global auth origin; TSAuthBaseURL overrides it.
	defaultInstance, err := defaultClient.TSAuth.Instance("acme")
	if err != nil {
		t.Fatal(err)
	}
	if defaultInstance.base != publicAuthBaseURL {
		t.Fatalf("default ts-auth base = %q", defaultInstance.base)
	}
	explicitClient, err := NewClient(Config{TSAuthBaseURL: "https://auth.example.com"})
	if err != nil {
		t.Fatal(err)
	}
	if _, err := explicitClient.TSAuth.Instance("acme"); err != nil {
		t.Fatalf("ts-auth with TSAuthBaseURL rejected: %v", err)
	}

	// G57: plain HTTP is allowed only for loopback (always) and .test hosts
	// (only with an explicit HTTPClient); a production http host is rejected
	// even with an explicit transport.
	if _, err := NewClient(Config{APIBaseURL: "http://localhost:9987"}); err != nil {
		t.Fatalf("http loopback rejected: %v", err)
	}
	if _, err := NewClient(Config{APIBaseURL: "http://127.0.0.1:9987"}); err != nil {
		t.Fatalf("http loopback IP rejected: %v", err)
	}
	if _, err := NewClient(Config{APIBaseURL: "http://[::1]:9987"}); err != nil {
		t.Fatalf("http IPv6 loopback rejected: %v", err)
	}
	if _, err := NewClient(Config{APIBaseURL: "http://auth.test"}); err == nil {
		t.Fatal("http .test host accepted without an explicit transport")
	}
	if _, err := NewClient(Config{APIBaseURL: "http://auth.test", HTTPClient: &http.Client{}}); err == nil {
		t.Fatal("injected transport accepted non-loopback HTTP")
	}
	for _, cfg := range []Config{
		{APIBaseURL: "http://auth.example.com"},
		{APIBaseURL: "http://auth.example.com", HTTPClient: &http.Client{}},
	} {
		if _, err := NewClient(cfg); err == nil {
			t.Fatalf("plain-http production host accepted: %+v", cfg)
		}
	}

	// G58: the base URL with and without a trailing slash behaves
	// identically.
	var requests []string
	slashServer := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		requests = append(requests, r.Method+" "+r.URL.RequestURI())
		tsAuthEdgeJSON(w, `{"sub":"01ARZ3NDEKTSV4RRFFQ69G5FA4"}`)
	}))
	defer slashServer.Close()
	for _, base := range []string{slashServer.URL, slashServer.URL + "/"} {
		instance := newTSAuthEdgeInstanceFor(t, base, Config{})
		if _, err := instance.OIDC.GetUserInfo(t.Context(), "access-token"); err != nil {
			t.Fatal(err)
		}
	}
	if !reflect.DeepEqual(requests, []string{"GET /acme/oauth/userinfo", "GET /acme/oauth/userinfo"}) {
		t.Fatalf("trailing-slash requests = %#v", requests)
	}

	// G60: mutating the Config record after construction does not affect the
	// client (NewClient snapshots it).
	snapshotRequests := 0
	cfgServer := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		snapshotRequests++
		tsAuthEdgeJSON(w, `{"sub":"01ARZ3NDEKTSV4RRFFQ69G5FA4"}`)
	}))
	defer cfgServer.Close()
	cfg := Config{TSAuthBaseURL: cfgServer.URL}
	client, err := NewClient(cfg)
	if err != nil {
		t.Fatal(err)
	}
	cfg.TSAuthBaseURL = "http://127.0.0.1:1/unreachable"
	snapshotInstance, err := client.TSAuth.Instance("acme")
	if err != nil {
		t.Fatal(err)
	}
	if _, err := snapshotInstance.OIDC.GetUserInfo(t.Context(), "access-token"); err != nil {
		t.Fatal(err)
	}
	if snapshotRequests != 1 {
		t.Fatalf("snapshot origin requests = %d, want 1", snapshotRequests)
	}
}

// G61: TSAuthBaseURL routes ts-auth traffic to the ts-auth origin, never the
// product origin.
func TestTSAuthEdgeExplicitOrigin(t *testing.T) {
	tsAuthRequests := 0
	tsAuthServer := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		tsAuthRequests++
		tsAuthEdgeJSON(w, `{"sub":"01ARZ3NDEKTSV4RRFFQ69G5FA4"}`)
	}))
	defer tsAuthServer.Close()
	productServer := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		t.Errorf("product origin hit: %s %s", r.Method, r.URL.RequestURI())
		http.NotFound(w, r)
	}))
	defer productServer.Close()

	client, err := NewClient(Config{APIBaseURL: productServer.URL, TSAuthBaseURL: tsAuthServer.URL})
	if err != nil {
		t.Fatal(err)
	}
	instance, err := client.TSAuth.Instance("acme")
	if err != nil {
		t.Fatal(err)
	}
	if _, err := instance.OIDC.GetUserInfo(t.Context(), "access-token"); err != nil {
		t.Fatal(err)
	}
	if tsAuthRequests != 1 {
		t.Fatalf("ts-auth origin requests = %d, want 1", tsAuthRequests)
	}
}

// B23: an oversized body keeps its observed status as a non-retryable
// response_too_large APIError.
func TestTSAuthEdgeOversizedResponse(t *testing.T) {
	attempts := 0
	instance, _ := newTSAuthEdgeInstance(t, Config{}, func(w http.ResponseWriter, r *http.Request, _ []byte) {
		attempts++
		w.Header().Set("Content-Length", strconv.Itoa(int(MaxResponseBodyBytes)+1))
		w.WriteHeader(http.StatusInternalServerError)
	})
	_, err := instance.OIDC.GetUserInfo(t.Context(), "access-token")
	tsAuthEdgeRequireAPIError(t, err, "tsauth.oidc.userinfo", http.StatusInternalServerError, "response_too_large",
		fmt.Sprintf("response body exceeds %d bytes", MaxResponseBodyBytes))
	if attempts != 1 {
		t.Fatalf("oversized attempts = %d, want exactly 1 (never retried)", attempts)
	}
}
