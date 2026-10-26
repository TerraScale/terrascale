package v1_test

import (
	"context"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"strconv"
	"strings"
	"testing"

	tb "terrascale.tech/sdk/v1"
)

var publicProbeCalls = []struct {
	path string
	call func(*tb.Probes, context.Context) (*tb.ProbeStatus, error)
}{{"healthz", (*tb.Probes).Health}, {"readyz", (*tb.Probes).Ready}, {"backupz", (*tb.Probes).Backup}}

func TestPublicHealthExactRequiredFields(t *testing.T) {
	for _, probe := range publicProbeCalls {
		for _, status := range []int{200, 503} {
			for _, body := range []string{
				`{"status":"future-status","version":"1","region":"future-region"}`,
				`{"status":"","version":"","region":"","storageFormat":{},"revision":false,"machine":["private-marker"],"Status":"ignored"}`,
			} {
				t.Run(probe.path+strconv.Itoa(status)+body, func(t *testing.T) {
					probes, calls, closes := publicHealthFixture(t, probe.path, status, body)
					out, err := probe.call(probes, t.Context())
					if out == nil || *calls != 1 || *closes != 1 {
						t.Fatalf("probe result/lifetime: %+v %v calls=%d closes=%d", out, err, *calls, *closes)
					}
					want := tb.ProbeStatus{Status: "future-status", Version: "1", Region: "future-region"}
					if strings.Contains(body, "private-marker") {
						want = tb.ProbeStatus{}
					}
					if *out != want {
						t.Fatalf("probe public body: %+v, want %+v", out, want)
					}
					if status == 200 && err != nil {
						t.Fatal(err)
					}
					if status == 503 {
						var api *tb.APIError
						if !errors.As(err, &api) || api.Status != 503 || string(api.Body) != body || api.RequestID != "probe-request" {
							t.Fatalf("503 body/error metadata: %v", err)
						}
					}
					encoded, err := json.Marshal(out)
					if err != nil || strings.Contains(string(encoded), "private-marker") || strings.Contains(string(encoded), "machine") {
						t.Fatal("private metadata became public")
					}
				})
			}
		}
	}
}

func TestPublicHealthRejectsMalformedRequiredFieldsAtomically(t *testing.T) {
	bodies := []string{`null`, `[]`, `{}`, `{"status":"x","version":"v","region":"r"} {}`}
	for _, key := range []string{"status", "version", "region"} {
		for _, invalid := range []json.RawMessage{nil, []byte(`null`), []byte(`123`), []byte(`true`), []byte(`[]`), []byte(`{"private-marker":true}`)} {
			fields := map[string]json.RawMessage{"status": []byte(`"s"`), "version": []byte(`"v"`), "region": []byte(`"r"`)}
			if invalid == nil {
				delete(fields, key)
				fields[strings.ToUpper(key)] = []byte(`"case-does-not-count"`)
			} else {
				fields[key] = invalid
			}
			body, err := json.Marshal(fields)
			if err != nil {
				t.Fatal(err)
			}
			bodies = append(bodies, string(body))
		}
	}
	for i, body := range bodies {
		t.Run(strconv.Itoa(i), func(t *testing.T) {
			initial := tb.ProbeStatus{Status: "original", Version: "original", Region: "original"}
			out := initial
			if err := out.UnmarshalJSON([]byte(body)); err == nil || out != initial || strings.Contains(err.Error(), "private-marker") {
				t.Fatalf("non-atomic or unsafe decode: %+v %v", out, err)
			}
			for _, probe := range publicProbeCalls {
				for _, status := range []int{200, 503} {
					probes, calls, closes := publicHealthFixture(t, probe.path, status, body)
					result, err := probe.call(probes, t.Context())
					if result != nil || err == nil || *calls != 1 || *closes != 1 || strings.Contains(err.Error(), "private-marker") {
						t.Fatalf("accepted malformed response: %+v %v calls=%d closes=%d", result, err, *calls, *closes)
					}
					if status == 503 {
						var api *tb.APIError
						if !errors.As(err, &api) || api.Status != 503 || string(api.Body) != body {
							t.Fatalf("lost 503 APIError/body: %v", err)
						}
					}
				}
			}
		})
	}
}

func TestPublicHealthPreservesCallerContext(t *testing.T) {
	type contextKey struct{}
	ctx, cancel := context.WithCancel(context.WithValue(t.Context(), contextKey{}, "caller"))
	defer cancel()
	calls := 0
	probes, err := tb.NewProbes(tb.Config{HTTPClient: &http.Client{Transport: publicHealthRoundTrip(func(r *http.Request) (*http.Response, error) {
		calls++
		if r.Context().Value(contextKey{}) != "caller" {
			t.Fatal("caller context lost")
		}
		cancel()
		return nil, r.Context().Err()
	})}})
	if err != nil {
		t.Fatal(err)
	}
	if out, err := probes.Ready(ctx); out != nil || !errors.Is(err, context.Canceled) || calls != 1 {
		t.Fatalf("in-flight cancellation: %+v %v calls=%d", out, err, calls)
	}
	if out, err := probes.Health(ctx); out != nil || !errors.Is(err, context.Canceled) || calls != 1 {
		t.Fatalf("pre-cancellation: %+v %v calls=%d", out, err, calls)
	}
}

func publicHealthFixture(t *testing.T, path string, status int, body string) (*tb.Probes, *int, *int) {
	t.Helper()
	calls, closes := 0, 0
	probes, err := tb.NewProbes(tb.Config{Token: "discarded-test-token", HTTPClient: &http.Client{Transport: publicHealthRoundTrip(func(r *http.Request) (*http.Response, error) {
		calls++
		if r.Method != http.MethodGet || r.URL.String() != tb.DefaultBaseURL+"/"+path || r.Body != nil || r.Header.Get("Authorization") != "" || r.Header.Get("Accept") != "application/json" {
			t.Fatal("public probe request contract changed")
		}
		return &http.Response{StatusCode: status, Header: http.Header{"Content-Type": {"application/json"}, "X-Request-Id": {"probe-request"}}, Body: &publicHealthBody{Reader: strings.NewReader(body), closes: &closes}}, nil
	})}})
	if err != nil {
		t.Fatal(err)
	}
	return probes, &calls, &closes
}

type publicHealthRoundTrip func(*http.Request) (*http.Response, error)

func (f publicHealthRoundTrip) RoundTrip(r *http.Request) (*http.Response, error) { return f(r) }

type publicHealthBody struct {
	io.Reader
	closes *int
}

func (b *publicHealthBody) Close() error { (*b.closes)++; return nil }
