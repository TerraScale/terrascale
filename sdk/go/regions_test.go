package sdk

import (
	"context"
	"errors"
	"io"
	"net/http"
	"slices"
	"strings"
	"testing"
	"testing/synctest"
	"time"
)

func TestRegionCatalogWireAndLifetime(t *testing.T) {
	calls, closed := 0, 0
	httpClient := &http.Client{Transport: pinningRoundTripperFunc(func(r *http.Request) (*http.Response, error) {
		calls++
		if r.Method != http.MethodGet || r.URL.String() != DefaultAPIBaseURL+"/v1/regions" || r.Body != nil ||
			r.Header.Get("Accept") != "application/json" || r.Header.Get("Authorization") != "" || r.Header.Get("X-TerraScale-Region-Pin") != "" {
			t.Errorf("unexpected catalog request: %s %s headers %v", r.Method, r.URL, r.Header)
		}
		body := `{"regions":["fra","gru","new-region9","sjc"]}`
		if calls > 1 {
			body = `{"regions":["gru"]}`
		}
		return &http.Response{StatusCode: 200, Header: http.Header{"Content-Type": {"application/json"}, "Cache-Control": {"public, max-age=5"}}, Body: &regionBody{Reader: strings.NewReader(body), closed: &closed}}, nil
	})}
	client, err := NewClient(Config{HTTPClient: httpClient})
	if err != nil {
		t.Fatal(err)
	}
	out, err := client.ListRegions(t.Context())
	if err != nil || out == nil || !slices.Equal(out.Regions, []string{"fra", "gru", "new-region9", "sjc"}) {
		t.Fatalf("catalog: %+v %v", out, err)
	}
	out, err = client.ListRegions(t.Context())
	if err != nil || out == nil || !slices.Equal(out.Regions, []string{"gru"}) || calls != 2 || closed != 2 {
		t.Fatalf("uncached catalog/lifetime: %+v %v calls=%d closed=%d", out, err, calls, closed)
	}
	if httpClient.CheckRedirect != nil {
		t.Fatal("caller HTTP client was mutated")
	}
}

func TestRegionCatalogFailuresAndCancellation(t *testing.T) {
	for _, body := range []string{`{}`, `null`, `{"regions":null}`, `{"regions":[]}`, `{"regions":[null]}`, `{"regions":["GRU"]}`, `{"regions":["gru","gru"]}`, `{"regions":["sjc","gru"]}`, `{"regions":[1]}`, `{"regions":["gru"]} {}`} {
		t.Run(body, func(t *testing.T) {
			client, _ := NewClient(Config{HTTPClient: &http.Client{Transport: pinningRoundTripperFunc(func(*http.Request) (*http.Response, error) {
				return &http.Response{StatusCode: 200, Header: http.Header{"Content-Type": {"application/json"}}, Body: io.NopCloser(strings.NewReader(body))}, nil
			})}})
			if out, err := client.ListRegions(t.Context()); err == nil || out != nil {
				t.Fatalf("accepted invalid catalog: %+v %v", out, err)
			}
		})
	}
	calls := 0
	client, _ := NewClient(Config{HTTPClient: &http.Client{Transport: pinningRoundTripperFunc(func(r *http.Request) (*http.Response, error) {
		calls++
		return &http.Response{StatusCode: 503, Header: make(http.Header), Body: io.NopCloser(strings.NewReader(`{"error":{"code":"unavailable","message":"unavailable"}}`))}, nil
	})}})
	_, err := client.ListRegions(t.Context())
	var apiErr *APIError
	if !errors.As(err, &apiErr) || apiErr.Status != 503 || apiErr.Code != "unavailable" || calls != 1 {
		t.Fatalf("error/replay: %v calls=%d", err, calls)
	}
	ctx, cancel := context.WithCancel(t.Context())
	cancel()
	if _, err := client.ListRegions(ctx); !errors.Is(err, context.Canceled) {
		t.Fatalf("cancellation: %v", err)
	}
	if calls != 1 {
		t.Fatal("pre-cancelled call dispatched")
	}
}

func TestRegionCatalogInFlightCancellation(t *testing.T) {
	entered := make(chan struct{})
	client, _ := NewClient(Config{HTTPClient: &http.Client{Transport: pinningRoundTripperFunc(func(r *http.Request) (*http.Response, error) {
		close(entered)
		<-r.Context().Done()
		return nil, r.Context().Err()
	})}})
	ctx, cancel := context.WithCancel(t.Context())
	defer cancel()
	done := make(chan error, 1)
	go func() { _, err := client.ListRegions(ctx); done <- err }()
	<-entered
	cancel()
	if err := <-done; !errors.Is(err, context.Canceled) {
		t.Fatalf("in-flight cancellation: %v", err)
	}
}

func TestRegionCatalogFixedDeadlinePreservesEarlierCallerDeadline(t *testing.T) {
	for _, callerLimit := range []time.Duration{time.Hour, time.Second} {
		t.Run(callerLimit.String(), func(t *testing.T) {
			ctx, cancel := context.WithTimeout(t.Context(), callerLimit)
			defer cancel()
			client, err := NewClient(Config{HTTPClient: &http.Client{Transport: pinningRoundTripperFunc(func(r *http.Request) (*http.Response, error) {
				latest := time.Now().Add(MaxRequestTimeout)
				if callerDeadline, _ := ctx.Deadline(); callerDeadline.Before(latest) {
					latest = callerDeadline
				}
				deadline, ok := r.Context().Deadline()
				if !ok || deadline.After(latest) || time.Until(deadline) <= 0 {
					t.Errorf("request deadline missing or extended: %v", deadline)
				}
				return &http.Response{StatusCode: 200, Header: make(http.Header), Body: io.NopCloser(strings.NewReader(`{"regions":["gru"]}`))}, nil
			})}})
			if err != nil {
				t.Fatal(err)
			}
			if _, err := client.ListRegions(ctx); err != nil {
				t.Fatal(err)
			}
		})
	}
}

func TestRegionCatalogResponseCapIsNotRetriedAndClosesBody(t *testing.T) {
	calls, closed := 0, 0
	client, err := NewClient(Config{HTTPClient: &http.Client{Transport: pinningRoundTripperFunc(func(*http.Request) (*http.Response, error) {
		calls++
		return &http.Response{StatusCode: 200, Header: http.Header{"X-Request-Id": {"catalog-cap"}}, ContentLength: MaxResponseBodyBytes + 1,
			Body: &regionBody{Reader: strings.NewReader(""), closed: &closed}}, nil
	})}})
	if err != nil {
		t.Fatal(err)
	}
	out, err := client.ListRegions(t.Context())
	var apiErr *APIError
	if out != nil || !errors.As(err, &apiErr) || apiErr.Code != "response_too_large" || apiErr.RequestID != "catalog-cap" || calls != 1 || closed != 1 {
		t.Fatalf("response cap/lifetime: out=%v err=%v calls=%d closed=%d", out, err, calls, closed)
	}
}

func TestRegionCatalogBodyReadErrorsPreserveContextAndMetadata(t *testing.T) {
	for _, status := range []int{200, 503} {
		for _, mode := range []string{"cancel", "deadline", "read"} {
			t.Run(mode+http.StatusText(status), func(t *testing.T) {
				synctest.Test(t, func(t *testing.T) {
					ctx, cancel := context.WithTimeout(t.Context(), time.Second)
					defer cancel()
					calls, closed, reads := 0, 0, 0
					client, err := NewClient(Config{HTTPClient: &http.Client{Transport: pinningRoundTripperFunc(func(r *http.Request) (*http.Response, error) {
						calls++
						body := &regionBody{closed: &closed, Reader: regionReadFunc(func(p []byte) (int, error) {
							reads++
							if ctx.Err() != nil {
								t.Fatal("context ended before body read began")
							}
							if mode == "read" {
								return copy(p, "synthetic-region-private"), errors.New("synthetic-region-private")
							}
							if mode == "cancel" {
								cancel()
							}
							select {
							case <-r.Context().Done():
								return 0, r.Context().Err()
							case <-time.After(5 * time.Second):
								return 0, errors.New("request context did not end")
							}
						})}
						return &http.Response{StatusCode: status, Header: http.Header{"X-Request-Id": {"catalog-read"}, "Retry-After": {"3"}}, Body: body}, nil
					})}})
					if err != nil {
						t.Fatal(err)
					}
					out, err := client.ListRegions(ctx)
					if out != nil || err == nil || calls != 1 || closed != 1 || reads != 1 {
						t.Fatalf("body read/lifetime: out=%v err=%v calls=%d closed=%d reads=%d", out, err, calls, closed, reads)
					}
					if strings.Contains(err.Error(), "synthetic-region-private") {
						t.Fatal("reader diagnostics or partial body escaped")
					}
					if mode != "read" {
						want := context.Canceled
						if mode == "deadline" {
							want = context.DeadlineExceeded
						}
						if !errors.Is(err, want) {
							t.Fatalf("context matching: got %v, want %v", err, want)
						}
						return
					}
					code := "response_read_failed"
					if status == 503 {
						code = "server_error"
					}
					var apiErr *APIError
					if !errors.As(err, &apiErr) || apiErr.Status != status || apiErr.Code != code || apiErr.RequestID != "catalog-read" || apiErr.RetryAfter == nil || *apiErr.RetryAfter != 3*time.Second || apiErr.Body != "" {
						t.Fatalf("read error metadata: %v", err)
					}
				})
			})
		}
	}
}

type regionReadFunc func([]byte) (int, error)

func (f regionReadFunc) Read(p []byte) (int, error) { return f(p) }

func TestRegionIDMatchesProducerFormatWithoutClosedList(t *testing.T) {
	for _, id := range []string{"fra", "gru", "sjc", "new-region9", "a", strings.Repeat("a", 63), " FRA "} {
		parsed, err := ParseRegionCode(id)
		if !IsRegionCode(id) || err != nil || parsed != RegionCode(strings.ToUpper(strings.TrimSpace(id))) {
			t.Errorf("rejected %q: %q %v", id, parsed, err)
		}
	}
	if RegionFRA != "FRA" {
		t.Fatal("FRA constant")
	}
	for _, id := range []string{"", "1gru", "-gru", "gru-", "g ru", "grü", "ſjc", "Kru", "gru.example", strings.Repeat("a", 64)} {
		if _, err := ParseRegionCode(id); IsRegionCode(id) || err == nil {
			t.Errorf("accepted malformed id %q", id)
		}
	}
}

type regionBody struct {
	io.Reader
	closed *int
}

func (b *regionBody) Close() error { (*b.closed)++; return nil }
