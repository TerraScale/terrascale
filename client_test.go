package sdk

import (
	"context"
	"encoding/json"
	"errors"
	"io"
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

func TestCoreTransportLearnsServingRegionAndPinsFollowingReadsAndWrites(t *testing.T) {
	var calls atomic.Int32
	transport := pinningRoundTripperFunc(func(r *http.Request) (*http.Response, error) {
		call := calls.Add(1)
		wantHost := "api.terrascale.tech"
		if call > 1 {
			wantHost = "sfo.api.terrascale.tech"
		}
		if r.URL.Host != wantHost {
			t.Errorf("request %d host = %q, want %q", call, r.URL.Host, wantHost)
		}
		headers := make(http.Header)
		if call == 1 {
			// Deliberately use a non-canonical map key: Header names are
			// case-insensitive on the wire and in the SDK trust boundary.
			headers["x-terrascale-serving-region"] = []string{" sFo "}
		}
		headers.Set("Content-Type", "application/json")
		return &http.Response{
			StatusCode: http.StatusOK,
			Header:     headers,
			Body:       io.NopCloser(strings.NewReader(`{}`)),
			Request:    r,
		}, nil
	})
	httpClient := &http.Client{Transport: transport}
	c, err := NewClient(Config{APIBaseURL: "https://api.terrascale.tech", HTTPClient: httpClient, AccessToken: "token"})
	if err != nil {
		t.Fatal(err)
	}
	if err := c.do(context.Background(), http.MethodGet, "/api/v1/read", nil, nil, nil, "read"); err != nil {
		t.Fatal(err)
	}
	if got := c.PinnedRegion(); got != RegionSFO {
		t.Fatalf("learned pin = %q, want %q", got, RegionSFO)
	}
	if err := c.do(context.Background(), http.MethodPost, "/api/v1/write", nil, map[string]string{"ok": "yes"}, nil, "write", WithIdempotencyKey("stable")); err != nil {
		t.Fatal(err)
	}
	if got := calls.Load(); got != 2 {
		t.Fatalf("calls = %d, want 2", got)
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

func TestCoreTransportDoesNotLearnMalformedOrMissingServingRegion(t *testing.T) {
	for _, value := range []string{"", "SFO,GRU", "https://sfo.api.terrascale.tech", "evil.invalid"} {
		t.Run(value, func(t *testing.T) {
			transport := pinningRoundTripperFunc(func(r *http.Request) (*http.Response, error) {
				headers := make(http.Header)
				if value != "" {
					headers["X-TERRASCALE-SERVING-REGION"] = []string{value}
				}
				headers.Set("Content-Type", "application/json")
				return &http.Response{
					StatusCode: http.StatusOK,
					Header:     headers,
					Body:       io.NopCloser(strings.NewReader(`{}`)),
					Request:    r,
				}, nil
			})
			c, err := NewClient(Config{APIBaseURL: "https://api.terrascale.tech", HTTPClient: &http.Client{Transport: transport}})
			if err != nil {
				t.Fatal(err)
			}
			if err := c.do(context.Background(), http.MethodGet, "/api/v1/read", nil, nil, nil, "read"); err != nil {
				t.Fatal(err)
			}
			if got := c.PinnedRegion(); got != "" {
				t.Fatalf("untrusted serving region %q was learned", got)
			}
		})
	}
}

func TestServingRegionObservationIsConcurrentAndDoesNotRepin(t *testing.T) {
	c, err := NewClient(Config{APIBaseURL: "https://api.terrascale.tech"})
	if err != nil {
		t.Fatal(err)
	}
	var group sync.WaitGroup
	for i := 0; i < 128; i++ {
		group.Add(1)
		go func() {
			defer group.Done()
			headers := make(http.Header)
			headers["x-terrascale-serving-region"] = []string{"sfo"}
			c.observeServingRegion(headers)
		}()
	}
	group.Wait()
	if got := c.PinnedRegion(); got != RegionSFO {
		t.Fatalf("concurrent learned pin = %q, want %q", got, RegionSFO)
	}
	// A later response cannot repin an already learned client.
	headers := make(http.Header)
	headers.Set("X-TerraScale-Region", "FRA")
	c.observeServingRegion(headers)
	if got := c.PinnedRegion(); got != RegionSFO {
		t.Fatalf("pin was repinned from %q to %q", RegionSFO, got)
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
