package v1_test

import (
	"context"
	"errors"
	"fmt"

	"net/http"
	"net/http/httptest"
	"sync/atomic"
	"testing"

	"terrascale.tech/sdk/tsauth"
	v1 "terrascale.tech/sdk/v1"
)

type contractTokenSource func(context.Context) (string, error)

func (f contractTokenSource) Token(ctx context.Context) (string, error) { return f(ctx) }

func TestTokenSourceExchangeToPublicAPI(t *testing.T) {
	const key = "01ARZ3NDEKTSV4RRFFQ69G5FA8_0123456789abcdefghjkmnpqrstvwxyz0123456789abcdefghjk"
	const token = "fixture.project.signature"
	var exchanges, requests atomic.Int64
	issuer := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		exchanges.Add(1)
		client, secret, ok := r.BasicAuth()
		if r.Method != http.MethodPost || r.URL.Path != "/terrascale/oauth/token" || !ok || client != key[:26] || secret != key {
			t.Error("incorrect local token exchange boundary")
		}
		if err := r.ParseForm(); err != nil || r.Form.Get("project") != testProject || r.Form.Get("scope") != "sql:read" {
			t.Error("project or permission changed during exchange")
		}
		fmt.Fprintf(w, `{"access_token":%q,"token_type":"Bearer","expires_in":900}`, token)
	}))
	t.Cleanup(issuer.Close)
	api := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		requests.Add(1)
		if r.Header.Get("Authorization") != "Bearer "+token || r.URL.Path != "/v1/projects/"+testProject+"/databases/main/sql/execute" {
			t.Error("wrong resource request or credential boundary")
		}
		fmt.Fprint(w, `{"results":[{"columns":[{"name":"n","decltype":"INTEGER"}],"rows":[[9007199254740993]],"rows_affected":0,"last_insert_rowid":null}]}`)
	}))
	t.Cleanup(api.Close)
	source, err := tsauth.NewAPIKeySource(tsauth.APIKeyConfig{Issuer: issuer.URL + "/terrascale", Key: key, Project: testProject, Scopes: []string{tsauth.ScopeSQLRead}})
	if err != nil {
		t.Fatal(err)
	}
	c, err := v1.New(v1.Config{BaseURL: api.URL, TokenSource: source})
	if err != nil {
		t.Fatal(err)
	}
	for range 2 {
		rows, err := c.Database(testProject, "main").Query(t.Context(), "SELECT n FROM t")
		if err != nil || len(rows) != 1 {
			t.Fatalf("rows=%v error=%v", rows, err)
		}
		if n, err := rows[0].Int64("n"); err != nil || n != 9007199254740993 {
			t.Fatalf("n=%d error=%v", n, err)
		}
	}
	if exchanges.Load() != 1 || requests.Load() != 2 {
		t.Fatalf("exchanges=%d requests=%d", exchanges.Load(), requests.Load())
	}
}

type rejectedContractSource struct{ calls, invalidations int }

func (s *rejectedContractSource) Token(context.Context) (string, error) {
	s.calls++
	return "fixture.jwt.signature", nil
}
func (s *rejectedContractSource) Invalidate(string) { s.invalidations++ }

func TestTokenSource401RefreshesOnceWithoutPermissionFallback(t *testing.T) {
	source := &rejectedContractSource{}
	var hits atomic.Int64
	peer := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		hits.Add(1)
		if r.Header.Get("Authorization") != "Bearer fixture.jwt.signature" {
			t.Error("credential fallback")
		}
		w.Header().Set("X-Request-ID", "final-denial")
		w.WriteHeader(http.StatusUnauthorized)
		fmt.Fprint(w, `{"error":{"code":"invalid_key"}}`)
	}))
	t.Cleanup(peer.Close)
	c, err := v1.New(v1.Config{BaseURL: peer.URL, TokenSource: source})
	if err != nil {
		t.Fatal(err)
	}
	_, err = c.Database(testProject, "main").Info(t.Context())
	var apiErr *v1.APIError
	if !errors.Is(err, v1.ErrInvalidKey) || !errors.As(err, &apiErr) || apiErr.RequestID != "final-denial" || hits.Load() != 2 || source.calls != 2 || source.invalidations != 1 {
		t.Fatalf("denial=%v hits=%d calls=%d invalidations=%d", err, hits.Load(), source.calls, source.invalidations)
	}
}

func TestUnsupportedScopeAndRevokedCredentialExchangeStopsBeforeResource(t *testing.T) {
	for _, code := range []string{"invalid_scope", "invalid_client", "invalid_target"} {
		t.Run(code, func(t *testing.T) {
			status := http.StatusBadRequest
			if code == "invalid_client" {
				status = http.StatusUnauthorized
			}
			issuer := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				if err := r.ParseForm(); err != nil || r.Form.Get("scope") != tsauth.ScopeSQLWrite {
					t.Error("permission was replaced")
				}
				w.WriteHeader(status)
				fmt.Fprintf(w, `{"error":%q}`, code)
			}))
			t.Cleanup(issuer.Close)
			source, err := tsauth.NewAPIKeySource(tsauth.APIKeyConfig{Issuer: issuer.URL + "/terrascale", Key: "01ARZ3NDEKTSV4RRFFQ69G5FA8_0123456789abcdefghjkmnpqrstvwxyz0123456789abcdefghjk", Project: testProject, Scopes: []string{tsauth.ScopeSQLWrite}})
			if err != nil {
				t.Fatal(err)
			}
			c, err := v1.New(v1.Config{TokenSource: source, HTTPClient: &http.Client{Transport: failTransport{t}}})
			if err != nil {
				t.Fatal(err)
			}
			_, err = c.Database(testProject, "main").Info(t.Context())
			var oauthErr *tsauth.Error
			if !errors.As(err, &oauthErr) || oauthErr.Code != code || oauthErr.Status != status {
				t.Fatalf("exchange=%v", err)
			}
		})
	}
}

func TestCancellationBeforeTokenOrHTTP(t *testing.T) {
	ctx, cancel := context.WithCancel(t.Context())
	cancel()
	c, err := v1.New(v1.Config{TokenSource: contractTokenSource(func(context.Context) (string, error) {
		t.Error("acquired token after cancellation")
		return "fixture.jwt.sig", nil
	}), HTTPClient: &http.Client{Transport: failTransport{t}}})
	if err != nil {
		t.Fatal(err)
	}
	_, err = c.Database(testProject, "main").Execute(ctx, "INSERT INTO t (id) VALUES (?)", "01ARZ3NDEKTSV4RRFFQ69G5FD2")
	if !errors.Is(err, context.Canceled) || errors.Is(err, v1.ErrCommitUncertain) {
		t.Fatalf("cancel=%v", err)
	}
}
