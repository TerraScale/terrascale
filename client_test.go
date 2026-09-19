package sdk

import (
	"context"
	"encoding/json"
	"errors"
	"net/http"
	"net/http/httptest"
	"net/url"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"
)

func TestCoreTransportUsesCanonicalSessionAndBearerHeaders(t *testing.T) {
	var calls atomic.Int32
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if got := r.Header.Get("Authorization"); got != "Bearer access-token" {
			t.Errorf("authorization = %q", got)
		}
		if got := r.Header.Get("X-TerraScale-Session-Token"); got != "" {
			t.Errorf("legacy session header was sent: %q", got)
		}
		switch calls.Add(1) {
		case 1:
			if got := r.Header.Get("X-TerraScale-Session"); got != "seed" {
				t.Errorf("first session = %q", got)
			}
			w.Header().Set("X-TerraScale-Session", "frontier-1")
		case 2:
			if got := r.Header.Get("X-TerraScale-Session"); got != "frontier-1" {
				t.Errorf("second session = %q", got)
			}
			w.Header().Set("X-TerraScale-Session", "frontier-2")
		default:
			if got := r.Header.Get("X-TerraScale-Session"); got != "frontier-2" {
				t.Errorf("third session = %q", got)
			}
		}
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write([]byte(`{}`))
	}))
	defer srv.Close()

	c, err := NewClient(Config{APIBaseURL: srv.URL, AccessToken: "access-token", SessionToken: "seed"})
	if err != nil {
		t.Fatal(err)
	}
	for i := 0; i < 3; i++ {
		if err := c.do(context.Background(), http.MethodGet, "/api/v1/ping", nil, nil, nil, "ping"); err != nil {
			t.Fatal(err)
		}
	}
	if got := c.SessionToken(); got != "frontier-2" {
		t.Fatalf("session frontier = %q", got)
	}
}

func TestSessionFrontierDoesNotRegressForNumericTokens(t *testing.T) {
	c, err := NewClient(Config{APIBaseURL: "http://127.0.0.1"})
	if err != nil {
		t.Fatal(err)
	}
	first := make(http.Header)
	first.Set("X-TerraScale-Session", "2")
	second := make(http.Header)
	second.Set("X-TerraScale-Session", "1")
	c.observeSession(first)
	c.observeSession(second)
	if got := c.SessionToken(); got != "2" {
		t.Fatalf("session frontier regressed to %q", got)
	}
}

func TestSessionFrontierUsesNumericTerraBaseSequencesConcurrently(t *testing.T) {
	c, err := NewClient(Config{APIBaseURL: "http://127.0.0.1"})
	if err != nil {
		t.Fatal(err)
	}
	var group sync.WaitGroup
	for sequence := 1; sequence <= 64; sequence++ {
		group.Add(1)
		go func(sequence int) {
			defer group.Done()
			headers := make(http.Header)
			headers.Set("X-TerraScale-Session", strconv.Itoa(sequence))
			c.observeSession(headers)
		}(sequence)
	}
	group.Wait()
	if got := c.SessionToken(); got != "64" {
		t.Fatalf("numeric session frontier = %q, want 64", got)
	}
}

func TestCoreTransportRepairsOneValidPlacementAndKeepsIdempotency(t *testing.T) {
	var calls atomic.Int32
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if got := r.Header.Get("Idempotency-Key"); got != "stable-write" {
			t.Errorf("idempotency key = %q", got)
		}
		if calls.Add(1) == 1 {
			w.Header().Set("X-TerraScale-Placement", " sFo ")
			w.WriteHeader(http.StatusMisdirectedRequest)
			return
		}
		if got := r.Header.Get("X-TerraScale-Preferred-Region"); got != "SFO" {
			t.Errorf("repaired region hint = %q", got)
		}
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write([]byte(`{}`))
	}))
	defer srv.Close()

	c, err := NewClient(Config{APIBaseURL: srv.URL})
	if err != nil {
		t.Fatal(err)
	}
	if err := c.do(context.Background(), http.MethodPost, "/api/v1/write", nil, map[string]string{"ok": "yes"}, nil, "write", WithIdempotencyKey("stable-write")); err != nil {
		t.Fatal(err)
	}
	if got := c.PinnedRegion(); got != RegionSFO {
		t.Fatalf("pinned region = %q", got)
	}
	if got := calls.Load(); got != 2 {
		t.Fatalf("calls = %d", got)
	}
}

func TestCoreTransportDoesNotReplayUnkeyedMutationAfterPlacement421(t *testing.T) {
	var calls atomic.Int32
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		calls.Add(1)
		w.Header().Set("X-TerraScale-Placement", "SFO")
		w.WriteHeader(http.StatusMisdirectedRequest)
	}))
	defer srv.Close()

	c, err := NewClient(Config{APIBaseURL: srv.URL})
	if err != nil {
		t.Fatal(err)
	}
	err = c.do(t.Context(), http.MethodPost, "/api/v1/write", nil, map[string]string{"ok": "yes"}, nil, "write")
	var apiErr *APIError
	if !errors.As(err, &apiErr) || apiErr.Status != http.StatusMisdirectedRequest {
		t.Fatalf("expected unkeyed mutation to return 421, got %v", err)
	}
	if got := calls.Load(); got != 1 {
		t.Fatalf("unkeyed mutation was replayed %d times", got)
	}
	if got := c.PinnedRegion(); got != RegionSFO {
		t.Fatalf("placement pin = %q, want %q", got, RegionSFO)
	}
}

func TestStrictAffinitySendsRegionHintThroughCustomProxy(t *testing.T) {
	var got string
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		got = r.Header.Get("X-TerraScale-Preferred-Region")
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write([]byte(`{}`))
	}))
	defer srv.Close()

	c, err := NewClient(Config{
		APIBaseURL:      srv.URL,
		Affinity:        AffinityStrict,
		PreferredRegion: RegionSFO,
	})
	if err != nil {
		t.Fatal(err)
	}
	if err := c.do(context.Background(), http.MethodGet, "/api/v1/read", nil, nil, nil, "read"); err != nil {
		t.Fatal(err)
	}
	if got != string(RegionSFO) {
		t.Fatalf("custom proxy region hint = %q, want %q", got, RegionSFO)
	}
}

type pinningRoundTripperFunc func(*http.Request) (*http.Response, error)

func (f pinningRoundTripperFunc) RoundTrip(r *http.Request) (*http.Response, error) {
	return f(r)
}

func TestCoreTransportDoesNotTrustInvalidPlacement(t *testing.T) {
	var calls atomic.Int32
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		calls.Add(1)
		w.Header().Set("X-TerraScale-Placement", "SFO,https://attacker.invalid")
		w.WriteHeader(http.StatusMisdirectedRequest)
	}))
	defer srv.Close()
	c, err := NewClient(Config{APIBaseURL: srv.URL})
	if err != nil {
		t.Fatal(err)
	}
	err = c.do(context.Background(), http.MethodGet, "/api/v1/read", nil, nil, nil, "read")
	var apiErr *APIError
	if !strings.Contains(err.Error(), "status 421") || !errors.As(err, &apiErr) {
		t.Fatalf("invalid placement error = %T %v", err, err)
	}
	if apiErr.Placement != "SFO" || c.PinnedRegion() != RegionSFO || calls.Load() != 2 {
		t.Fatalf("placement list was not repaired deterministically: err=%+v pin=%q calls=%d", apiErr, c.PinnedRegion(), calls.Load())
	}
}

func TestPlacementRegionSelectsFirstSupportedRegionFromList(t *testing.T) {
	headers := make(http.Header)
	headers.Set("X-TerraScale-Placement", "unknown, fra, sfo")
	if got, ok := placementRegion(headers); !ok || got != RegionFRA {
		t.Fatalf("placementRegion = %q, %v", got, ok)
	}
	if got, ok := placementRegionExcluding(headers, RegionFRA); !ok || got != RegionSFO {
		t.Fatalf("placementRegionExcluding = %q, %v", got, ok)
	}
}

func TestErrorFromResponseDecodesBoundedNestedDiagnostic(t *testing.T) {
	headers := make(http.Header)
	headers.Set("X-Request-ID", "request-1")
	err := errorFromResponse("documents.get", http.MethodGet, "/api/v1/documents/1", http.StatusConflict, headers, []byte(`{"error":{"code":"conflict","message":"document changed","fix":"refresh and retry"}}`))
	if err.Code != "conflict" || err.Message != "document changed" || err.Fix != "refresh and retry" || err.RequestID != "request-1" {
		t.Fatalf("decoded error = %+v", err)
	}
}

func TestRetryAfterIsBoundedAndRejectsInvalidValues(t *testing.T) {
	for _, test := range []struct {
		value string
		want  time.Duration
		ok    bool
	}{
		{"2", 2 * time.Second, true},
		{"999999999999999999999999", MaxRetryAfter, true},
		{"-1", 0, false},
		{"1.5", 0, false},
	} {
		got, ok := parseRetryAfter(test.value)
		if got != test.want || ok != test.ok {
			t.Errorf("parseRetryAfter(%q) = (%s, %v), want (%s, %v)", test.value, got, ok, test.want, test.ok)
		}
	}
}

func TestCoreTransportRejectsOversizedRequestBeforeSending(t *testing.T) {
	var calls atomic.Int32
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { calls.Add(1) }))
	defer srv.Close()
	c, err := NewClient(Config{APIBaseURL: srv.URL})
	if err != nil {
		t.Fatal(err)
	}
	tooLarge := strings.Repeat("x", int(MaxRequestBodyBytes))
	err = c.do(context.Background(), http.MethodPost, "/api/v1/write", url.Values{}, tooLarge, nil, "write")
	if err == nil || !strings.Contains(err.Error(), "request body exceeds") {
		t.Fatalf("oversized request error = %v", err)
	}
	if calls.Load() != 0 {
		t.Fatal("oversized request reached server")
	}
}

func TestCoreTransportRejectsOversizedResponse(t *testing.T) {
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write([]byte(`{"data":"` + strings.Repeat("x", int(MaxResponseBodyBytes)) + `"}`))
	}))
	defer srv.Close()
	c, err := NewClient(Config{APIBaseURL: srv.URL})
	if err != nil {
		t.Fatal(err)
	}
	err = c.do(context.Background(), http.MethodGet, "/api/v1/read", nil, nil, &json.RawMessage{}, "read")
	if err == nil || !strings.Contains(err.Error(), "response body exceeds") {
		t.Fatalf("oversized response error = %v", err)
	}
}

func TestNewClientValidatesRequestTimeoutCeiling(t *testing.T) {
	if _, err := NewClient(Config{APIBaseURL: "http://127.0.0.1", RequestTimeout: MaxRequestTimeout + time.Nanosecond}); err == nil {
		t.Fatal("request timeout above protocol ceiling was accepted")
	}
	if _, err := NewClient(Config{APIBaseURL: "http://127.0.0.1", SessionToken: strings.Repeat("x", MaxSessionTokenBytes+1)}); err == nil {
		t.Fatal("oversized session token was accepted")
	}
}

func TestNewClientRejectsInsecureOrCredentialBearingEndpoints(t *testing.T) {
	for _, endpoint := range []string{
		"http://api.terrascale.tech",
		"https://user:secret@api.terrascale.tech",
		"https://api.terrascale.tech/?access_token=leaked",
	} {
		if _, err := NewClient(Config{APIBaseURL: endpoint}); err == nil {
			t.Fatalf("unsafe endpoint %q was accepted", endpoint)
		}
	}
	if _, err := NewClient(Config{APIBaseURL: "http://api.example.test", HTTPClient: &http.Client{}}); err != nil {
		t.Fatalf("explicit test transport should allow .test HTTP endpoint: %v", err)
	}
}

func TestStrictAffinityRejectsPlacementRedirect(t *testing.T) {
	var calls atomic.Int32
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		calls.Add(1)
		if got := r.Header.Get("X-TerraScale-Preferred-Region"); got != "GRU" {
			t.Errorf("region hint = %q", got)
		}
		w.Header().Set("X-TerraScale-Placement", "FRA")
		w.WriteHeader(http.StatusMisdirectedRequest)
	}))
	defer srv.Close()
	client, err := NewClient(Config{APIBaseURL: srv.URL, PreferredRegion: RegionGRU, Affinity: AffinityStrict})
	if err != nil {
		t.Fatal(err)
	}
	err = client.do(t.Context(), http.MethodGet, "/api/v1/regions", nil, nil, nil, "regions.list")
	var apiErr *APIError
	if !errors.As(err, &apiErr) || apiErr.Status != http.StatusMisdirectedRequest {
		t.Fatalf("expected 421, got %v", err)
	}
	if calls.Load() != 1 || client.EffectiveRegion() != RegionGRU {
		t.Fatalf("strict request escaped: calls=%d region=%s", calls.Load(), client.EffectiveRegion())
	}
}

func TestClientDoesNotFollowCredentialOrMutationRedirects(t *testing.T) {
	for _, test := range []struct {
		method string
		status int
	}{{http.MethodGet, http.StatusFound}, {http.MethodPut, http.StatusTemporaryRedirect}, {http.MethodPut, http.StatusPermanentRedirect}} {
		t.Run(test.method+strconv.Itoa(test.status), func(t *testing.T) {
			destinationCalls := 0
			destination := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { destinationCalls++; w.WriteHeader(204) }))
			defer destination.Close()
			source := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				if r.Header.Get("Authorization") != "Bearer secret" {
					t.Error("initial credential missing")
				}
				http.Redirect(w, r, destination.URL, test.status)
			}))
			defer source.Close()
			originalRedirectCalls := 0
			custom := &http.Client{CheckRedirect: func(*http.Request, []*http.Request) error { originalRedirectCalls++; return nil }}
			client, err := NewClient(Config{APIBaseURL: source.URL, AccessToken: "secret", HTTPClient: custom})
			if err != nil {
				t.Fatal(err)
			}
			err = client.do(t.Context(), test.method, "/api/v1/databases/demo", nil, map[string]string{"value": "private"}, nil, "redirect-regression", WithIdempotencyKey("write-1"))
			var apiErr *APIError
			if !errors.As(err, &apiErr) || apiErr.Status != test.status || destinationCalls != 0 || originalRedirectCalls != 0 {
				t.Fatalf("redirect followed: err=%v destinations=%d callbacks=%d", err, destinationCalls, originalRedirectCalls)
			}
			_ = custom.CheckRedirect(nil, nil)
			if originalRedirectCalls != 1 {
				t.Fatal("SDK mutated the supplied HTTP client's redirect policy")
			}
		})
	}
}

func TestCreateOnlyDocumentPUTAndScopeSelectors(t *testing.T) {
	calls := 0
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		calls++
		if r.Method != "PUT" || r.Header.Get("If-None-Match") != "*" {
			t.Errorf("missing create-only precondition: %s %v", r.Method, r.Header)
		}
		if r.Header.Get("X-TerraScale-Tenant-ID") != "tenant-1" || r.Header.Get("X-TerraScale-Project-ID") != "project-1" {
			t.Error("scope selectors missing")
		}
		w.Header().Set("Content-Type", "application/json")
		if calls == 1 {
			_, _ = w.Write([]byte(`{"revision":"r1"}`))
			return
		}
		w.WriteHeader(412)
		_, _ = w.Write([]byte(`{"error":{"code":"precondition_failed","message":"document already exists","fix":"use an update operation"}}`))
	}))
	defer server.Close()
	client, err := NewClient(Config{APIBaseURL: server.URL, TenantID: "tenant-1", ProjectID: "project-1"})
	if err != nil {
		t.Fatal(err)
	}
	if _, err := client.Documents.Create(t.Context(), "demo", "orders", "order-1", map[string]int{"count": 1}); err != nil {
		t.Fatal(err)
	}
	if _, err := client.Documents.Put(t.Context(), "demo", "orders", "order-1", map[string]int{"count": 2}, WithCreateOnly()); err == nil {
		t.Fatal("precondition failure hidden")
	}
	for _, method := range []string{"PATCH", "DELETE", "POST"} {
		if err := client.do(t.Context(), method, "/api/v1/databases/demo/collections/orders/documents/order-1", nil, nil, nil, "documents.put", WithCreateOnly()); err == nil {
			t.Fatalf("create-only accepted for %s", method)
		}
	}
	if calls != 2 {
		t.Fatalf("unsupported precondition reached HTTP: %d", calls)
	}
	if _, err := NewClient(Config{TenantID: "bad\nselector"}); err == nil {
		t.Fatal("invalid tenant selector accepted")
	}
}
