package tsauth

import (
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/http/httptest"
	"net/url"

	"sync"
	"sync/atomic"
	"testing"
	"time"
)

const testKey = "01ARZ3NDEKTSV4RRFFQ69G5FA8_0123456789abcdefghjkmnpqrstvwxyz0123456789abcdefghjk"

// tokenServer is a token endpoint of the contract (section 6). handle
// answers each request after checking it; hits counts requests.
type tokenServer struct {
	*httptest.Server
	hits atomic.Int64
}

func newTokenServer(t *testing.T, handle func(w http.ResponseWriter, r *http.Request, form url.Values)) *tokenServer {
	s := &tokenServer{}
	s.Server = httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		s.hits.Add(1)
		if r.Method != http.MethodPost || r.URL.Path != "/terrascale/oauth/token" || r.Header.Get("Content-Type") != "application/x-www-form-urlencoded" {
			t.Errorf("request %s %s %s", r.Method, r.URL.Path, r.Header.Get("Content-Type"))
		}
		body, _ := io.ReadAll(r.Body)
		form, err := url.ParseQuery(string(body))
		if err != nil {
			t.Error(err)
		}
		handle(w, r, form)
	}))
	t.Cleanup(s.Close)
	return s
}

func answer(w http.ResponseWriter, token string, expiresIn int) {
	w.Header().Set("Content-Type", "application/json")
	fmt.Fprintf(w, `{"access_token":%q,"token_type":"Bearer","expires_in":%d,"scope":"sql:read"}`, token, expiresIn)
}

func TestAPIKeySourceCachesUntilAMinuteBeforeExpiry(t *testing.T) {
	var srv *tokenServer
	srv = newTokenServer(t, func(w http.ResponseWriter, r *http.Request, form url.Values) {
		id, secret, ok := r.BasicAuth()
		if !ok || id != testKey[:26] || secret != testKey || form.Get("grant_type") != "client_credentials" ||
			form.Get("project") != "01K6SY8PVAFJ6SW3RT5CY4TQKF" || form.Get("scope") != "sql:read sql:write" {
			t.Errorf("client credentials request: %q %q %v", id, secret, form)
		}
		answer(w, fmt.Sprintf("token-%d", srv.hits.Load()), 900)
	})
	s, err := NewAPIKeySource(APIKeyConfig{Issuer: srv.URL + "/terrascale", Key: testKey,
		Project: "01K6SY8PVAFJ6SW3RT5CY4TQKF", Scopes: []string{ScopeSQLRead, ScopeSQLWrite}})
	if err != nil {
		t.Fatal(err)
	}
	clock := time.Now()
	s.now = func() time.Time { return clock }
	for range 3 {
		if tok, err := s.Token(t.Context()); err != nil || tok != "token-1" {
			t.Fatalf("token: %q %v", tok, err)
		}
	}
	clock = clock.Add(839 * time.Second)
	if tok, _ := s.Token(t.Context()); tok != "token-1" || srv.hits.Load() != 1 {
		t.Fatalf("before refresh: %q, %d requests", tok, srv.hits.Load())
	}
	clock = clock.Add(time.Second) // 60 s before expiry: refreshed in the background
	if tok, _ := s.Token(t.Context()); tok != "token-1" {
		t.Fatalf("during refresh: %q", tok)
	}
	waitIdle(t, s)
	if tok, _ := s.Token(t.Context()); tok != "token-2" || srv.hits.Load() != 2 {
		t.Fatalf("after refresh: %q, %d requests", tok, srv.hits.Load())
	}
}

// waitIdle waits until s has no token request in flight.
func waitIdle(t *testing.T, s *TokenSource) {
	t.Helper()
	for deadline := time.Now().Add(5 * time.Second); ; time.Sleep(time.Millisecond) {
		s.mu.Lock()
		idle := s.flight == nil
		s.mu.Unlock()
		if idle {
			return
		}
		if time.Now().After(deadline) {
			t.Fatal("token request still in flight")
		}
	}
}

// refresh starts; the token is valid for 30 s more

// 11 s left: still handed out

// 10 s left: callers wait for a new token

type roundTripFunc func(*http.Request) (*http.Response, error)

func (f roundTripFunc) RoundTrip(r *http.Request) (*http.Response, error) { return f(r) }

// the response takes 2 s

func TestConcurrentCallersShareOneRequest(t *testing.T) {
	release := make(chan struct{})
	srv := newTokenServer(t, func(w http.ResponseWriter, r *http.Request, form url.Values) {
		<-release
		answer(w, "shared", 900)
	})
	s, err := NewAPIKeySource(APIKeyConfig{Issuer: srv.URL + "/terrascale", Key: testKey})
	if err != nil {
		t.Fatal(err)
	}
	var wg sync.WaitGroup
	tokens := make([]string, 16)
	for i := range tokens {
		wg.Go(func() { tokens[i], _ = s.Token(t.Context()) })
	}
	time.Sleep(50 * time.Millisecond)
	close(release)
	wg.Wait()
	for _, tok := range tokens {
		if tok != "shared" {
			t.Fatalf("tokens: %q", tokens)
		}
	}
	if n := srv.hits.Load(); n != 1 {
		t.Fatalf("%d requests", n)
	}
}

func TestInvalidClientIsTerminalButTransientErrorsAreNotCached(t *testing.T) {
	var status atomic.Int64
	status.Store(http.StatusUnauthorized)
	srv := newTokenServer(t, func(w http.ResponseWriter, r *http.Request, form url.Values) {
		switch code := int(status.Load()); code {
		case http.StatusOK:
			answer(w, "ok", 900)
		case http.StatusTooManyRequests:
			w.Header().Set("Retry-After", "60") // beyond the request deadline: not retried
			w.WriteHeader(code)
			io.WriteString(w, `{"error":"rate_limited"}`)
		default:
			w.WriteHeader(code)
			io.WriteString(w, `{"error":"invalid_client","error_description":"unknown key"}`)
		}
	})
	s, err := NewAPIKeySource(APIKeyConfig{Issuer: srv.URL + "/terrascale", Key: testKey})
	if err != nil {
		t.Fatal(err)
	}
	_, err = s.Token(t.Context())
	var e *Error
	if !errors.As(err, &e) || e.Status != 401 || e.Code != "invalid_client" || e.Description != "unknown key" {
		t.Fatalf("401: %v", err)
	}
	status.Store(http.StatusTooManyRequests)
	if _, err = s.Token(t.Context()); !errors.As(err, &e) || e.Code != "invalid_client" || srv.hits.Load() != 1 {
		t.Fatalf("terminal key refusal: %v, %d requests", err, srv.hits.Load())
	}
	s, err = NewAPIKeySource(APIKeyConfig{Issuer: srv.URL + "/terrascale", Key: testKey})
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.Token(t.Context()); !errors.As(err, &e) || e.Code != "rate_limited" || e.RetryAfter != 60*time.Second {
		t.Fatalf("429: %v", err)
	}
	status.Store(http.StatusOK)
	if tok, err := s.Token(t.Context()); err != nil || tok != "ok" || srv.hits.Load() != 3 {
		t.Fatalf("recovered: %q %v, %d requests", tok, err, srv.hits.Load())
	}
}

func TestNoRedirects(t *testing.T) {
	elsewhere := httptest.NewServer(http.HandlerFunc(func(http.ResponseWriter, *http.Request) {
		t.Error("the key followed a redirect")
	}))
	t.Cleanup(elsewhere.Close)
	srv := newTokenServer(t, func(w http.ResponseWriter, r *http.Request, form url.Values) {
		http.Redirect(w, r, elsewhere.URL+"/terrascale/oauth/token", http.StatusTemporaryRedirect)
	})
	s, err := NewAPIKeySource(APIKeyConfig{Issuer: srv.URL + "/terrascale", Key: testKey, HTTPClient: &http.Client{}})
	if err != nil {
		t.Fatal(err)
	}
	var e *Error
	if _, err := s.Token(t.Context()); !errors.As(err, &e) || e.Status != http.StatusTemporaryRedirect {
		t.Fatalf("redirect: %v", err)
	}
}
