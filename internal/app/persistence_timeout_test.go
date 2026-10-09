package app

import (
	"bytes"
	"context"
	"errors"
	"io"
	"net/http"
	"net/http/httptest"
	"net/http/httputil"
	"net/url"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"terrascale.tech/dashboard/internal/adapters/terrabase"
	"terrascale.tech/dashboard/internal/platform/config"
	"terrascale.tech/dashboard/internal/platform/terrabasetest"
)

type persistenceTransportFunc func(*http.Request) (*http.Response, error)

func (f persistenceTransportFunc) RoundTrip(r *http.Request) (*http.Response, error) {
	return f(r)
}

func TestTerraBasePersistenceRequestTimeout(t *testing.T) {
	if testing.Short() {
		t.Skip("starts pinned TerraBase release")
	}
	tb := terrabasetest.Start(t)
	target, err := url.Parse(tb.URL)
	if err != nil {
		t.Fatal(err)
	}
	for _, tc := range []struct {
		name    string
		custom  bool
		timeout time.Duration
	}{
		{name: "nil_client", timeout: 15 * time.Second},
		{name: "custom_timeout", custom: true, timeout: 250 * time.Millisecond},
		{name: "custom_without_timeout", custom: true},
	} {
		t.Run(tc.name, func(t *testing.T) {
			db, key := terrabasetest.Database(t, tb, "dashboard")
			if err := terrabase.EnsureCollections(t.Context(), db); err != nil {
				t.Fatal(err)
			}
			forward := httputil.NewSingleHostReverseProxy(target)
			forward.Transport = tb.Client.Transport
			var fault atomic.Bool
			var attempts atomic.Int32
			started := make(chan struct{}, 1)
			canceled := make(chan time.Duration, 1)
			release := make(chan struct{})
			var releaseOnce sync.Once
			unblock := func() { releaseOnce.Do(func() { close(release) }) }
			isCommit := func(r *http.Request) bool {
				return fault.Load() && r.Method == http.MethodPost && strings.HasSuffix(r.URL.Path, "/transactions")
			}
			proxy := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				if isCommit(r) {
					// Consume the body so net/http can detect the client's disconnect.
					body, err := io.ReadAll(r.Body)
					if err != nil {
						t.Errorf("read proxy transaction body: %v", err)
						return
					}
					r.Body = io.NopCloser(bytes.NewReader(body))
					attempt := attempts.Add(1)
					at := time.Now()
					if attempt == 1 {
						started <- struct{}{}
					}
					select {
					case <-r.Context().Done():
						select {
						case canceled <- time.Since(at):
						default:
						}
						return
					case <-release:
					}
				}
				forward.ServeHTTP(w, r)
			}))
			t.Cleanup(func() { unblock(); proxy.Close() })
			cfg := config.Config{
				TerraBaseURL: proxy.URL, TerraBaseAPIKey: key, TerraBaseProjectID: db.Project(), TerraBaseDatabase: db.Name(),
				CSRFSecret: []byte(strings.Repeat("r", 32)),
			}
			deadlines := make(chan time.Time, 1)
			redirectError := errors.New("caller redirect policy")
			if tc.custom {
				cfg.TerraBaseHTTPClient = &http.Client{
					Timeout:       tc.timeout,
					CheckRedirect: func(*http.Request, []*http.Request) error { return redirectError },
					Transport: persistenceTransportFunc(func(r *http.Request) (*http.Response, error) {
						if isCommit(r) {
							deadline, _ := r.Context().Deadline()
							select {
							case deadlines <- deadline:
							default:
							}
						}
						return http.DefaultTransport.RoundTrip(r)
					}),
				}
			}
			application, err := New(cfg)
			if err != nil {
				t.Fatal(err)
			}
			issue := func(ctx context.Context, response *httptest.ResponseRecorder) {
				request := httptest.NewRequestWithContext(ctx, http.MethodPost,
					"http://localhost/api/v1/dashboard/cli/device-authorization", strings.NewReader("client_id=terrascale-cli"))
				request.Header.Set("Content-Type", "application/x-www-form-urlencoded")
				application.Handler.ServeHTTP(response, request)
			}
			healthy := httptest.NewRecorder()
			issue(t.Context(), healthy)
			if healthy.Code != http.StatusOK || !strings.Contains(healthy.Body.String(), `"device_code"`) {
				t.Fatalf("healthy persistence = %d %s", healthy.Code, healthy.Body)
			}

			// Stall a write: SDK read retries would multiply the 15-second budget.
			fault.Store(true)
			ctx, cancel := context.WithCancel(context.Background())
			response := httptest.NewRecorder()
			done := make(chan struct{})
			go func() { issue(ctx, response); close(done) }()
			defer func() {
				cancel()
				unblock()
				select {
				case <-done:
				case <-time.After(5 * time.Second):
					t.Error("persistence handler did not stop during cleanup")
				}
			}()
			// This watchdog only fails the test; it cannot cancel the SDK request.
			watchdog := time.NewTimer(20 * time.Second)
			defer watchdog.Stop()
			select {
			case <-started:
			case <-watchdog.C:
				t.Fatal("persistence transaction never reached the local stall")
			}
			if tc.custom {
				var deadline time.Time
				select {
				case deadline = <-deadlines:
				case <-watchdog.C:
					t.Fatal("caller transport was replaced")
				}
				if tc.timeout == 0 {
					if !deadline.IsZero() {
						t.Fatalf("caller client without timeout acquired deadline %s", deadline)
					}
					fault.Store(false)
					unblock()
				} else if deadline.IsZero() || time.Until(deadline) > tc.timeout {
					t.Fatalf("caller timeout %s replaced: request deadline = %s", tc.timeout, deadline)
				}
				if cfg.TerraBaseHTTPClient.Timeout != tc.timeout || cfg.TerraBaseHTTPClient.CheckRedirect == nil || cfg.TerraBaseHTTPClient.CheckRedirect(nil, nil) != redirectError {
					t.Fatal("caller HTTP client was modified")
				}
			}
			select {
			case <-done:
			case <-watchdog.C:
				t.Fatalf("stalled persistence request exceeded 20s; caller context error = %v", ctx.Err())
			}
			if ctx.Err() != nil {
				t.Fatalf("caller cancellation hid the client timeout: %v", ctx.Err())
			}
			if tc.custom && tc.timeout == 0 {
				if response.Code != http.StatusOK || !strings.Contains(response.Body.String(), `"device_code"`) {
					t.Fatalf("released persistence = %d %s", response.Code, response.Body)
				}
			} else {
				if response.Code != http.StatusTooManyRequests || !strings.Contains(response.Body.String(), `"error":"slow_down"`) {
					t.Fatalf("failed persistence must refuse authorization = %d %s", response.Code, response.Body)
				}
				select {
				case elapsed := <-canceled:
					if elapsed < tc.timeout-min(time.Second, tc.timeout/2) || elapsed > tc.timeout+3*time.Second {
						t.Fatalf("stalled transaction canceled after %s, want client timeout %s", elapsed, tc.timeout)
					}
					t.Logf("stalled persistence canceled after %s (client timeout %s); caller context remains active", elapsed, tc.timeout)
				case <-watchdog.C:
					t.Fatal("client returned without canceling the stalled transaction")
				}
			}
			if got := attempts.Load(); got != 1 {
				t.Fatalf("stalled transaction attempts = %d, want 1 (no uncertain write replay)", got)
			}
		})
	}
}
