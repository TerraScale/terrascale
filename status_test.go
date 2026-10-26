package sdk

import (
	"context"
	"errors"
	"io"
	"net/http"
	"net/http/httptest"
	"strconv"
	"strings"
	"sync/atomic"
	"testing"
	"time"
)

const latencyFixture = `{"generated_at":"2026-10-08T12:00:00Z","window":"1h","regions":[
	{"code":"gru","name":"São Paulo","ping_url":"https://gru.terrascale.tech/ping","status":"operational",
	 "consensus":{"median_ms":12,"p95_ms":25,"samples":100},"vantages":[
		{"code":"sfo","name":"San Francisco","median_ms":0,"p95_ms":1,"samples":50},
		{"code":"fra","name":"Frankfurt","median_ms":null,"p95_ms":null,"samples":0}]},
	{"code":"sfo","name":"San Francisco","ping_url":null,"status":"planned","consensus":null,"vantages":[]}
]}`

func TestStatusLatency(t *testing.T) {
	for _, test := range []struct{ region, query string }{
		{"", ""}, {" GRU ", "gru"}, {"new-region", "new-region"}, {"gru&extra=yes", "gru&extra=yes"},
	} {
		t.Run(test.region, func(t *testing.T) {
			server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				if r.Method != http.MethodGet || r.URL.Path != "/api/latency" || r.URL.Query().Get("region") != test.query {
					t.Errorf("unexpected request: %s %s", r.Method, r.URL)
				}
				wantKeys := 0
				if test.query != "" {
					wantKeys = 1
				}
				if len(r.URL.Query()) != wantKeys || r.Header.Get("Authorization") != "" || r.Header.Get("Accept") != "application/json" {
					t.Error("request must contain only the optional region query and no credentials")
				}
				w.Header().Set("Content-Type", "application/json")
				io.WriteString(w, latencyFixture)
			}))
			t.Cleanup(server.Close)
			client, err := NewClient(Config{StatusBaseURL: server.URL + "/", TSAuthBaseURL: "https://auth.example.test"})
			if err != nil {
				t.Fatal(err)
			}
			out, err := client.Status.Latency(t.Context(), test.region)
			if err != nil {
				t.Fatal(err)
			}
			if out.GeneratedAt.Format(time.RFC3339) != "2026-10-08T12:00:00Z" || out.Window != "1h" || len(out.Regions) != 2 {
				t.Fatalf("unexpected response: %+v", out)
			}
			region := out.Regions[0]
			if region.Code != "gru" || region.Name != "São Paulo" || region.PingURL == nil || *region.PingURL != "https://gru.terrascale.tech/ping" || region.Status != "operational" || region.Consensus == nil {
				t.Fatalf("unexpected region: %+v", region)
			}
			stats := region.Consensus
			if stats.MedianMS == nil || *stats.MedianMS != 12 || stats.P95MS == nil || *stats.P95MS != 25 || stats.Samples != 100 || len(region.Vantages) != 2 {
				t.Fatalf("unexpected measurements: %+v", stats)
			}
			vantage := region.Vantages[0]
			if vantage.Code != "sfo" || vantage.Name != "San Francisco" || vantage.MedianMS == nil || *vantage.MedianMS != 0 || vantage.P95MS == nil || *vantage.P95MS != 1 || vantage.Samples != 50 {
				t.Fatalf("unexpected vantage: %+v", vantage)
			}
			missing := region.Vantages[1]
			if missing.MedianMS != nil || missing.P95MS != nil || missing.Samples != 0 || out.Regions[1].Consensus != nil || out.Regions[1].PingURL != nil {
				t.Fatal("missing measurements were converted to measured zeroes")
			}
		})
	}
}

func TestStatusBaseURL(t *testing.T) {
	for _, base := range []string{"", "https://auth.terrascale.tech"} {
		t.Run(base, func(t *testing.T) {
			wantBase := base
			if wantBase == "" {
				wantBase = DefaultStatusBaseURL
			}
			client, err := NewClient(Config{StatusBaseURL: base, HTTPClient: &http.Client{Transport: pinningRoundTripperFunc(func(r *http.Request) (*http.Response, error) {
				if r.URL.String() != wantBase+"/api/latency" {
					t.Errorf("status must use its own origin without auth discovery: %s", r.URL)
				}
				return &http.Response{StatusCode: http.StatusOK, Header: http.Header{"Content-Type": {"application/json"}}, Body: io.NopCloser(strings.NewReader(latencyFixture))}, nil
			})}})
			if err != nil {
				t.Fatal(err)
			}
			if _, err := client.Status.Latency(t.Context(), ""); err != nil {
				t.Fatal(err)
			}
		})
	}
	for _, base := range []string{"/relative", "http://status.terrascale.tech", "ftp://example.test", "https://user:password@example.test", "https://example.test/path", "https://example.test?", "https://example.test?region=gru", "https://example.test#", "https://example.test#fragment"} {
		_, err := NewClient(Config{StatusBaseURL: base})
		var validationErr *ValidationError
		if !errors.As(err, &validationErr) || validationErr.Field != "status_base_url" {
			t.Errorf("accepted invalid status origin %q", base)
		}
		if base == "https://example.test/path" {
			continue // Auth explicitly permits proxy path prefixes.
		}
		client, err := NewClient(Config{TSAuthBaseURL: base})
		if err != nil {
			t.Fatal(err)
		}
		_, err = client.TSAuth.Instance("acme")
		if !errors.As(err, &validationErr) || validationErr.Field != "ts_auth_base_url" {
			t.Errorf("accepted invalid auth base %q", base)
		}
	}
}

func TestServiceBasePathPolicy(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/proxy/acme/oauth/userinfo" {
			t.Errorf("auth proxy prefix lost: %s", r.URL.Path)
		}
		w.Header().Set("Content-Type", "application/json")
		io.WriteString(w, `{"sub":"01ARZ3NDEKTSV4RRFFQ69G5FA1"}`)
	}))
	t.Cleanup(server.Close)
	client, err := NewClient(Config{TSAuthBaseURL: " " + server.URL + "/proxy/ "})
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
	if _, err := NewClient(Config{StatusBaseURL: server.URL + "/proxy/"}); err == nil {
		t.Fatal("status accepted a proxy path prefix")
	}
}

func TestStatusErrorsAndRetries(t *testing.T) {
	for _, test := range []struct {
		name                   string
		status, retries, calls int
		succeed                bool
	}{
		{"transient recovery", 503, 1, 2, true},
		{"rate limit recovery", 429, 1, 2, true},
		{"exhausted retries", 503, 3, 4, false},
		{"not found", 404, 2, 1, false},
		{"redirect refused", 302, 2, 1, false},
	} {
		t.Run(test.name, func(t *testing.T) {
			var calls atomic.Int32
			server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				call := calls.Add(1)
				w.Header().Set("Content-Type", "application/json")
				if test.succeed && call == int32(test.calls) {
					io.WriteString(w, latencyFixture)
					return
				}
				w.Header().Set("Retry-After", "0")
				w.Header().Set("X-Request-ID", "latency-request")
				w.Header().Set("Location", "/redirected")
				w.WriteHeader(test.status)
				io.WriteString(w, `{"error":{"code":"latency_unavailable","message":"try later","fix":"retry"}}`)
			}))
			t.Cleanup(server.Close)
			client, err := NewClient(Config{StatusBaseURL: server.URL})
			if err != nil {
				t.Fatal(err)
			}
			out, err := client.Status.Latency(t.Context(), "gru")
			if test.succeed {
				if err != nil || out == nil {
					t.Fatalf("retry failed: %v", err)
				}
			} else {
				var apiErr *APIError
				if out != nil || !errors.As(err, &apiErr) || apiErr.Status != test.status || apiErr.Code != "latency_unavailable" || apiErr.Message != "try later" || apiErr.Fix != "retry" || apiErr.Route != "status.latency" || apiErr.RequestID != "latency-request" || apiErr.RetryAfter == nil || *apiErr.RetryAfter != 0 {
					t.Fatalf("unexpected error: %v", err)
				}
			}
			if calls.Load() != int32(test.calls) {
				t.Errorf("requests = %d, want %d", calls.Load(), test.calls)
			}
		})
	}
}

func TestStatusInvalidResponses(t *testing.T) {
	for _, test := range []struct{ name, body string }{
		{"empty", ""}, {"malformed", "not JSON"}, {"timestamp", `{"generated_at":"invalid"}`},
		{"fractional milliseconds", strings.Replace(latencyFixture, `"median_ms":12`, `"median_ms":12.5`, 1)},
	} {
		t.Run(test.name, func(t *testing.T) {
			server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				w.Header().Set("Content-Type", "application/json")
				io.WriteString(w, test.body)
			}))
			t.Cleanup(server.Close)
			client, err := NewClient(Config{StatusBaseURL: server.URL})
			if err != nil {
				t.Fatal(err)
			}
			if out, err := client.Status.Latency(t.Context(), ""); err == nil || out != nil {
				t.Fatal("accepted invalid response")
			}
		})
	}
}

func TestStatusContextAndTimeout(t *testing.T) {
	for _, retry := range []bool{false, true} {
		t.Run(strconv.FormatBool(retry), func(t *testing.T) {
			var calls atomic.Int32
			server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				calls.Add(1)
				if retry {
					w.Header().Set("Retry-After", "30")
					w.WriteHeader(http.StatusServiceUnavailable)
					return
				}
				<-r.Context().Done()
			}))
			t.Cleanup(server.Close)
			client, err := NewClient(Config{StatusBaseURL: server.URL})
			if err != nil {
				t.Fatal(err)
			}
			deadlineCtx, deadlineCancel := context.WithTimeout(t.Context(), 100*time.Millisecond)
			defer deadlineCancel()
			out, err := client.Status.Latency(deadlineCtx, "")
			var refused *RetryBudgetError
			if out != nil || (!retry && !errors.Is(err, context.DeadlineExceeded)) || (retry && !errors.As(err, &refused)) {
				t.Fatalf("request timeout not honored: %v", err)
			}
			ctx, cancel := context.WithCancel(t.Context())
			cancel()
			if _, err := client.Status.Latency(ctx, ""); !errors.Is(err, context.Canceled) {
				t.Fatalf("canceled context not honored: %v", err)
			}
			if _, err := client.Status.Latency(nil, ""); err == nil {
				t.Fatal("accepted nil context")
			}
			if calls.Load() != 1 {
				t.Errorf("requests = %d, want 1", calls.Load())
			}
		})
	}
}

func TestStatusResponseLimit(t *testing.T) {
	var calls atomic.Int32
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		calls.Add(1)
		w.Header().Set("Content-Length", strconv.FormatInt(MaxResponseBodyBytes+1, 10))
	}))
	t.Cleanup(server.Close)
	client, err := NewClient(Config{StatusBaseURL: server.URL})
	if err != nil {
		t.Fatal(err)
	}
	_, err = client.Status.Latency(t.Context(), "")
	var apiErr *APIError
	if !errors.As(err, &apiErr) || apiErr.Code != "response_too_large" || calls.Load() != 1 {
		t.Fatalf("oversized response was not refused without retry: %v", err)
	}
}
