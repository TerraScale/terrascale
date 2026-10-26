package tsauth

import (
	"context"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/http/httptest"
	"net/url"
	"strings"
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
	fmt.Fprintf(w, `{"access_token":%q,"token_type":"Bearer","expires_in":%d,"scope":"documents:read"}`, token, expiresIn)
}

func TestAPIKeySourceCachesUntilAMinuteBeforeExpiry(t *testing.T) {
	var srv *tokenServer
	srv = newTokenServer(t, func(w http.ResponseWriter, r *http.Request, form url.Values) {
		id, secret, ok := r.BasicAuth()
		if !ok || id != testKey[:26] || secret != testKey || form.Get("grant_type") != "client_credentials" ||
			form.Get("project") != "01K6SY8PVAFJ6SW3RT5CY4TQKF" || form.Get("scope") != "documents:read documents:write" {
			t.Errorf("client credentials request: %q %q %v", id, secret, form)
		}
		answer(w, fmt.Sprintf("token-%d", srv.hits.Load()), 900)
	})
	s, err := NewAPIKeySource(APIKeyConfig{Issuer: srv.URL + "/terrascale", Key: testKey,
		Project: "01K6SY8PVAFJ6SW3RT5CY4TQKF", Scopes: []string{ScopeDocumentsRead, ScopeDocumentsWrite}})
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
	clock = clock.Add(time.Second) // 60 s before expiry
	if tok, _ := s.Token(t.Context()); tok != "token-2" || srv.hits.Load() != 2 {
		t.Fatalf("after refresh: %q, %d requests", tok, srv.hits.Load())
	}
}

func TestTokenExpiredInTransit(t *testing.T) {
	var mu sync.Mutex
	clock := time.Now()
	srv := newTokenServer(t, func(w http.ResponseWriter, r *http.Request, form url.Values) {
		mu.Lock()
		clock = clock.Add(2 * time.Second) // the response takes 2 s
		mu.Unlock()
		answer(w, "short", 1)
	})
	s, err := NewAPIKeySource(APIKeyConfig{Issuer: srv.URL + "/terrascale", Key: testKey})
	if err != nil {
		t.Fatal(err)
	}
	s.now = func() time.Time { mu.Lock(); defer mu.Unlock(); return clock }
	if tok, err := s.Token(t.Context()); err == nil {
		t.Fatalf("expired token %q returned", tok)
	}
	if _, err := s.Token(t.Context()); err == nil || srv.hits.Load() != 2 {
		t.Fatalf("expired token cached: %v, %d requests", err, srv.hits.Load())
	}
}

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

func TestCallerCancelLeavesTheRequestToOthers(t *testing.T) {
	release := make(chan struct{})
	srv := newTokenServer(t, func(w http.ResponseWriter, r *http.Request, form url.Values) {
		<-release
		answer(w, "kept", 900)
	})
	s, err := NewAPIKeySource(APIKeyConfig{Issuer: srv.URL + "/terrascale", Key: testKey})
	if err != nil {
		t.Fatal(err)
	}
	ctx, cancel := context.WithCancel(t.Context())
	done := make(chan error, 1)
	go func() { _, err := s.Token(ctx); done <- err }()
	live := make(chan string, 1)
	go func() { token, _ := s.Token(t.Context()); live <- token }()
	deadline := time.Now().Add(time.Second)
	for {
		s.mu.Lock()
		joined := s.flight != nil && s.flight.waiters == 2
		s.mu.Unlock()
		if joined {
			break
		}
		if time.Now().After(deadline) {
			close(release)
			t.Fatal("waiters did not join")
		}
		time.Sleep(time.Millisecond)
	}
	cancel()
	if err := <-done; !errors.Is(err, context.Canceled) {
		t.Fatal(err)
	}
	close(release)
	if token := <-live; token != "kept" || srv.hits.Load() != 1 {
		t.Fatalf("live caller: %q, %d requests", token, srv.hits.Load())
	}
}

func TestInvalidate(t *testing.T) {
	var srv *tokenServer
	srv = newTokenServer(t, func(w http.ResponseWriter, r *http.Request, form url.Values) {
		answer(w, fmt.Sprintf("token-%d", srv.hits.Load()), 900)
	})
	s, err := NewAPIKeySource(APIKeyConfig{Issuer: srv.URL + "/terrascale", Key: testKey})
	if err != nil {
		t.Fatal(err)
	}
	first, _ := s.Token(t.Context())
	s.Invalidate("another token")
	if tok, _ := s.Token(t.Context()); tok != first {
		t.Fatalf("kept token: %q", tok)
	}
	s.Invalidate(first)
	if tok, _ := s.Token(t.Context()); tok == first || srv.hits.Load() != 2 {
		t.Fatalf("after invalidate: %q, %d requests", tok, srv.hits.Load())
	}
}

func TestTokenEndpointErrorsAreNotCached(t *testing.T) {
	var status atomic.Int64
	status.Store(http.StatusUnauthorized)
	srv := newTokenServer(t, func(w http.ResponseWriter, r *http.Request, form url.Values) {
		switch code := int(status.Load()); code {
		case http.StatusOK:
			answer(w, "ok", 900)
		case http.StatusTooManyRequests:
			w.Header().Set("Retry-After", "7")
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
	if _, err = s.Token(t.Context()); !errors.As(err, &e) || e.Code != "rate_limited" || e.RetryAfter != 7*time.Second {
		t.Fatalf("429: %v", err)
	}
	status.Store(http.StatusOK)
	if tok, err := s.Token(t.Context()); err != nil || tok != "ok" || srv.hits.Load() != 3 {
		t.Fatalf("recovered: %q %v, %d requests", tok, err, srv.hits.Load())
	}
}

func TestRejectsInvalidTokenResponses(t *testing.T) {
	for name, body := range map[string]string{
		"mac token":     `{"access_token":"secret-mac","token_type":"mac","expires_in":900}`,
		"no expiry":     `{"access_token":"secret-x","token_type":"Bearer"}`,
		"overflowing":   `{"access_token":"secret-x","token_type":"Bearer","expires_in":9223372037}`,
		"numeric token": `{"access_token":1234567890123456789,"token_type":"Bearer","expires_in":900}`,
		"huge expiry":   `{"access_token":"secret-x","token_type":"Bearer","expires_in":98765432109876543210}`,
		"not json":      `access_token=secret-x`,
	} {
		t.Run(name, func(t *testing.T) {
			srv := newTokenServer(t, func(w http.ResponseWriter, r *http.Request, form url.Values) { io.WriteString(w, body) })
			s, err := NewAPIKeySource(APIKeyConfig{Issuer: srv.URL + "/terrascale", Key: testKey})
			if err != nil {
				t.Fatal(err)
			}
			_, err = s.Token(t.Context())
			if err == nil {
				t.Fatal("accepted")
			}
			for _, leak := range []string{"secret", "1234567890123456789", "98765432109876543210", "9223372037"} {
				if strings.Contains(err.Error(), leak) || strings.Contains(fmt.Sprintf("%#v", errors.Unwrap(err)), leak) {
					t.Fatalf("error exposes %q: %v", leak, err)
				}
			}
		})
	}
}

func TestExchangeSource(t *testing.T) {
	for _, confidential := range []bool{true, false} {
		t.Run(fmt.Sprint("confidential=", confidential), func(t *testing.T) {
			srv := newTokenServer(t, func(w http.ResponseWriter, r *http.Request, form url.Values) {
				id, secret, basic := r.BasicAuth()
				if confidential != basic || (basic && (id != "ts-dashboard" || secret != "s3cret")) || (!basic && form.Get("client_id") != "ts-cli") {
					t.Errorf("client authentication: basic=%t %q %q %v", basic, id, secret, form)
				}
				if form.Get("grant_type") != "urn:ietf:params:oauth:grant-type:token-exchange" || form.Get("subject_token") != "user-token" ||
					form.Get("subject_token_type") != "urn:ietf:params:oauth:token-type:access_token" ||
					form.Get("organization") != "01ARZ3NDEKTSV4RRFFQ69G5FA6" || form.Get("project") != "01ARZ3NDEKTSV4RRFFQ69G5FA7" || form.Get("scope") != "documents:read" {
					t.Errorf("exchange form: %v", form)
				}
				answer(w, "project-token", 900)
			})
			cfg := ExchangeConfig{Issuer: srv.URL + "/terrascale", ClientID: "ts-cli", Organization: "01ARZ3NDEKTSV4RRFFQ69G5FA6", Project: "01ARZ3NDEKTSV4RRFFQ69G5FA7",
				Scopes: []string{ScopeDocumentsRead}, SubjectToken: func(context.Context) (string, error) { return "user-token", nil }}
			if confidential {
				cfg.ClientID, cfg.ClientSecret = "ts-dashboard", "s3cret"
			}
			s, err := NewExchangeSource(cfg)
			if err != nil {
				t.Fatal(err)
			}
			if tok, err := s.Token(t.Context()); err != nil || tok != "project-token" {
				t.Fatalf("exchange: %q %v", tok, err)
			}
		})
	}
	failure := errors.New("signed out")
	s, err := NewExchangeSource(ExchangeConfig{Issuer: "https://auth.example/terrascale", ClientID: "ts-cli", Organization: "01ARZ3NDEKTSV4RRFFQ69G5FA6",
		SubjectToken: func(context.Context) (string, error) { return "", failure }})
	if err != nil {
		t.Fatal(err)
	}
	if _, err := s.Token(t.Context()); !errors.Is(err, failure) {
		t.Fatalf("subject failure: %v", err)
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

func TestConfigRejection(t *testing.T) {
	subject := func(context.Context) (string, error) { return "", nil }
	for name, err := range map[string]error{
		"old key ID":        second(NewAPIKeySource(APIKeyConfig{Key: "tsk_3k9f2m8q1x7v4c6n0b5z_0123456789abcdefghjkmnpqrstvwxyz0123456789abcdefghjk"})),
		"prefixed project":  second(NewAPIKeySource(APIKeyConfig{Key: testKey, Project: "prj_01ARZ3NDEKTSV4RRFFQ69G5FAV"})),
		"exchange project":  second(NewExchangeSource(ExchangeConfig{ClientID: "ts-cli", Organization: "01ARZ3NDEKTSV4RRFFQ69G5FA6", Project: "legacy", SubjectToken: subject})),
		"UUID organization": second(NewExchangeSource(ExchangeConfig{ClientID: "ts-cli", Organization: "0190a6e4-7c3b-7d2e-8f00-0123456789ab", SubjectToken: subject})),
		"short key":         second(NewAPIKeySource(APIKeyConfig{Key: "tsk_123"})),
		"tsk_ prefixed key": second(NewAPIKeySource(APIKeyConfig{Key: "tsk_" + testKey})),
		"key scope":         second(NewAPIKeySource(APIKeyConfig{Key: testKey, Scopes: []string{"shapes:read"}})),
		"key scope case":    second(NewAPIKeySource(APIKeyConfig{Key: testKey, Scopes: []string{"Documents:Read"}})),
		"exchange scope":    second(NewExchangeSource(ExchangeConfig{ClientID: "ts-cli", Organization: "01ARZ3NDEKTSV4RRFFQ69G5FA6", SubjectToken: subject, Scopes: []string{"openid"}})),
		"tb_ key":           second(NewAPIKeySource(APIKeyConfig{Key: "tb_" + strings.Repeat("a", 43)})),
		"key with l":        second(NewAPIKeySource(APIKeyConfig{Key: strings.Replace(testKey, "0", "l", 1)})),
		"http issuer":       second(NewAPIKeySource(APIKeyConfig{Key: testKey, Issuer: "http://auth.example/terrascale"})),
		"issuer query":      second(NewAPIKeySource(APIKeyConfig{Key: testKey, Issuer: "https://auth.example/terrascale?x=1"})),
		"empty query":       second(NewAPIKeySource(APIKeyConfig{Key: testKey, Issuer: "https://auth.example/terrascale?"})),
		"escaped slash":     second(NewAPIKeySource(APIKeyConfig{Key: testKey, Issuer: "https://auth.example/tenant%2Fname"})),
		"no client":         second(NewExchangeSource(ExchangeConfig{Organization: "01ARZ3NDEKTSV4RRFFQ69G5FA6", SubjectToken: subject})),
		"no organization":   second(NewExchangeSource(ExchangeConfig{ClientID: "ts-cli", SubjectToken: subject})),
		"no subject":        second(NewExchangeSource(ExchangeConfig{ClientID: "ts-cli", Organization: "01ARZ3NDEKTSV4RRFFQ69G5FA6"})),
	} {
		if err == nil {
			t.Errorf("%s: no error", name)
		} else if !strings.HasPrefix(err.Error(), "tsauth:") {
			t.Errorf("%s: missing error context", name)
		}
	}
	if s, err := NewAPIKeySource(APIKeyConfig{Key: testKey}); err != nil || s == nil {
		t.Fatalf("default issuer: %v", err)
	}
	for issuer, want := range map[string]string{
		"":                                 "https://auth.terrascale.tech/terrascale/oauth/token",
		"https://auth.example/terrascale/": "https://auth.example/terrascale/oauth/token",
	} {
		if got, err := tokenEndpoint(issuer); err != nil || got != want {
			t.Errorf("tokenEndpoint(%q) = %q, %v; want %q", issuer, got, err, want)
		}
	}
}

func TestRetryAfter(t *testing.T) {
	future := time.Now().Add(time.Minute).UTC().Format(http.TimeFormat)
	for v, want := range map[string][2]time.Duration{
		"7":                             {7 * time.Second, 7 * time.Second},
		"0":                             {0, 0},
		"":                              {0, 0},
		"-1":                            {0, 0},
		"soon":                          {0, 0},
		"Wed, 21 Oct 2015 07:28:00 GMT": {0, 0},
		future:                          {58 * time.Second, time.Minute},
	} {
		if got := retryAfter(v); got < want[0] || got > want[1] {
			t.Errorf("retryAfter(%q) = %v, want %v-%v", v, got, want[0], want[1])
		}
	}
}

func second[T any](_ T, err error) error { return err }
