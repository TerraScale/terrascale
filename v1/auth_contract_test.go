package v1_test

import (
	"context"
	"errors"
	"fmt"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync/atomic"
	"terrascale.tech/sdk/tsauth"
	"testing"

	v1 "terrascale.tech/sdk/v1"
)

type invalidatingSource struct{ tokens, invalidations atomic.Int64 }

func (s *invalidatingSource) Token(context.Context) (string, error) {
	s.tokens.Add(1)
	return "fixture.project.signature", nil
}
func (s *invalidatingSource) Invalidate(string) { s.invalidations.Add(1) }

// The TerraBase audience is the default API origin.
func TestContractAudienceIsDefaultBaseURL(t *testing.T) {
	t.Parallel()
	if v1.DefaultBaseURL != "https://api.terrascale.tech" {
		t.Fatalf("DefaultBaseURL = %q", v1.DefaultBaseURL)
	}
}

// A 401 (authentication failed) refreshes the token once; a 403
// (authenticated, missing scope or wrong project) is surfaced as is,
// without re-authenticating.
func TestContract401And403AreDistinct(t *testing.T) {
	t.Parallel()
	for _, tc := range []struct {
		status                        int
		body                          string
		code                          v1.ErrorCode
		requests, tokens, invalidated int64
	}{
		{http.StatusUnauthorized, `{"error":{"code":"invalid_key","message":"denied"}}`, v1.ErrInvalidKey, 2, 2, 1},
		{http.StatusUnauthorized, `{"error":{"code":"key_revoked","message":"denied"}}`, v1.ErrKeyRevoked, 1, 1, 1},
		{http.StatusUnauthorized, `{"error":{"code":"key_expired","message":"denied"}}`, v1.ErrKeyExpired, 1, 1, 1},
		{http.StatusForbidden, `{"error":{"code":"forbidden_scope","message":"denied"}}`, v1.ErrForbiddenScope, 1, 1, 0},
	} {
		t.Run(http.StatusText(tc.status), func(t *testing.T) {
			t.Parallel()
			var requests atomic.Int64
			api := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				requests.Add(1)
				w.Header().Set("Content-Type", "application/json")
				w.WriteHeader(tc.status)
				w.Write([]byte(tc.body))
			}))
			t.Cleanup(api.Close)
			source := &invalidatingSource{}
			c, err := v1.New(v1.Config{BaseURL: api.URL, TokenSource: source})
			if err != nil {
				t.Fatal(err)
			}
			_, err = c.Database(testProject, "main").Collection("users").Get(t.Context(), "01ARZ3NDEKTSV4RRFFQ69G5FAV")
			var apiErr *v1.APIError
			if !errors.As(err, &apiErr) || apiErr.Status != tc.status || !errors.Is(err, tc.code) {
				t.Fatalf("error = %v", err)
			}
			if requests.Load() != tc.requests || source.tokens.Load() != tc.tokens || source.invalidations.Load() != tc.invalidated {
				t.Fatalf("requests=%d tokens=%d invalidations=%d", requests.Load(), source.tokens.Load(), source.invalidations.Load())
			}
		})
	}
}

func TestKeyRefusalStopsCachedExchangeAndAuthorityOutageRetries(t *testing.T) {
	t.Parallel()
	for _, code := range []v1.ErrorCode{v1.ErrKeyRevoked, v1.ErrKeyExpired, v1.ErrAuthUnavailable} {
		t.Run(string(code), func(t *testing.T) {
			t.Parallel()
			var exchanges, requests atomic.Int64
			api := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				w.Header().Set("Content-Type", "application/json")
				if strings.HasSuffix(r.URL.Path, "/oauth/token") {
					exchanges.Add(1)
					fmt.Fprint(w, `{"access_token":"fixture.project.signature","token_type":"Bearer","expires_in":3600}`)
					return
				}
				n := requests.Add(1)
				if code == v1.ErrAuthUnavailable && n > 1 {
					fmt.Fprint(w, `{"id":"01ARZ3NDEKTSV4RRFFQ69G5FAV","version":"v1","state":"live","document":{}}`)
					return
				}
				w.Header().Set("Retry-After", "0")
				w.Header().Set("WWW-Authenticate", `Bearer error="invalid_token"`)
				status := http.StatusUnauthorized
				if code == v1.ErrAuthUnavailable {
					status = http.StatusServiceUnavailable
				}
				w.WriteHeader(status)
				fmt.Fprintf(w, `{"error":{"code":%q,"message":"key refused"}}`, string(code))
			}))
			t.Cleanup(api.Close)
			source, err := tsauth.NewAPIKeySource(tsauth.APIKeyConfig{Issuer: api.URL, Key: "01ARZ3NDEKTSV4RRFFQ69G5FAV_" + strings.Repeat("a", 52)})
			if err != nil {
				t.Fatal(err)
			}
			c, err := v1.New(v1.Config{BaseURL: api.URL, TokenSource: source})
			if err != nil {
				t.Fatal(err)
			}
			_, err = c.Database(testProject, "main").Collection("users").Get(t.Context(), "01ARZ3NDEKTSV4RRFFQ69G5FAV")
			if code == v1.ErrAuthUnavailable {
				if err != nil || requests.Load() != 2 {
					t.Fatalf("requests=%d error=%v", requests.Load(), err)
				}
			} else {
				var apiErr *v1.APIError
				if !errors.Is(err, code) || !errors.As(err, &apiErr) || apiErr.Message != "key refused" {
					t.Fatalf("error=%v", err)
				}
				if _, err = source.Token(t.Context()); !errors.Is(err, code) {
					t.Fatalf("cached error=%v", err)
				}
				if requests.Load() != 1 {
					t.Fatalf("requests=%d", requests.Load())
				}
			}
			if exchanges.Load() != 1 {
				t.Fatalf("exchanges=%d", exchanges.Load())
			}
		})
	}
}
