package v1_test

import (
	"context"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
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
		if err := r.ParseForm(); err != nil || r.Form.Get("project") != testProject || r.Form.Get("scope") != "documents:read" {
			t.Error("project or permission changed during exchange")
		}
		fmt.Fprintf(w, `{"access_token":%q,"token_type":"Bearer","expires_in":900}`, token)
	}))
	t.Cleanup(issuer.Close)
	api := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		requests.Add(1)
		if r.Header.Get("Authorization") != "Bearer "+token || r.URL.Path != "/v1/projects/"+testProject+"/databases/main/collections/users/documents/by-key" || r.URL.Query().Get("id") != "01ARZ3NDEKTSV4RRFFQ69G5FD2" {
			t.Error("wrong resource request or credential boundary")
		}
		fmt.Fprint(w, `{"id":"01ARZ3NDEKTSV4RRFFQ69G5FD2","version":"tbv1-fixture","state":"value","document":{"id":"application","n":9007199254740993}}`)
	}))
	t.Cleanup(api.Close)
	source, err := tsauth.NewAPIKeySource(tsauth.APIKeyConfig{Issuer: issuer.URL + "/terrascale", Key: key, Project: testProject, Scopes: []string{tsauth.ScopeDocumentsRead}})
	if err != nil {
		t.Fatal(err)
	}
	c, err := v1.New(v1.Config{BaseURL: api.URL, TokenSource: source})
	if err != nil {
		t.Fatal(err)
	}
	for range 2 {
		doc, err := c.Database(testProject, "main").Collection("users").Get(t.Context(), "01ARZ3NDEKTSV4RRFFQ69G5FD2")
		if err != nil || !strings.Contains(string(doc.Document), "9007199254740993") {
			t.Fatalf("document=%+v error=%v", doc, err)
		}
	}
	if exchanges.Load() != 1 || requests.Load() != 2 {
		t.Fatalf("exchanges=%d requests=%d", exchanges.Load(), requests.Load())
	}
}

func TestTokenSourceFailuresDoNotSendOrReportUncertainCommit(t *testing.T) {
	failure := errors.New("token exchange denied")
	for _, tc := range []struct {
		name, token string
		err         error
	}{
		{"denied", "", failure}, {"empty", "", nil}, {"malformed", "not-a-jwt", nil}, {"legacy key", testToken, nil},
	} {
		t.Run(tc.name, func(t *testing.T) {
			c, err := v1.New(v1.Config{TokenSource: contractTokenSource(func(context.Context) (string, error) { return tc.token, tc.err }), HTTPClient: &http.Client{Transport: failTransport{t}}})
			if err != nil {
				t.Fatal(err)
			}
			_, err = c.Database(testProject, "main").Collection("users").Put(t.Context(), "01ARZ3NDEKTSV4RRFFQ69G5FD2", map[string]any{})
			if err == nil || errors.Is(err, v1.ErrCommitUncertain) || tc.err != nil && !errors.Is(err, failure) {
				t.Fatalf("error=%v", err)
			}
		})
	}
	if _, err := v1.New(v1.Config{Token: testToken, TokenSource: contractTokenSource(func(context.Context) (string, error) { return "fixture.jwt.sig", nil })}); err == nil {
		t.Fatal("accepted ambiguous credential sources")
	}
}

type rejectedContractSource struct{ calls, invalidations int }

func (s *rejectedContractSource) Token(context.Context) (string, error) {
	s.calls++
	return "fixture.jwt.signature", nil
}
func (s *rejectedContractSource) Invalidate(string) { s.invalidations++ }

func TestTokenSource401RefreshesOnceWithoutPermissionFallback(t *testing.T) {
	for _, stream := range []bool{false, true} {
		t.Run(fmt.Sprint(stream), func(t *testing.T) {
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
			db := c.Database(testProject, "main")
			if stream {
				for _, e := range db.Changes(t.Context(), v1.ChangesOptions{}) {
					err = e
				}
			} else {
				_, err = db.Collection("users").Get(t.Context(), "01ARZ3NDEKTSV4RRFFQ69G5FD2")
			}
			var apiErr *v1.APIError
			if !errors.Is(err, v1.ErrInvalidKey) || !errors.As(err, &apiErr) || apiErr.RequestID != "final-denial" || hits.Load() != 2 || source.calls != 2 || source.invalidations != 1 {
				t.Fatalf("denial=%v hits=%d calls=%d invalidations=%d", err, hits.Load(), source.calls, source.invalidations)
			}
		})
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
				if err := r.ParseForm(); err != nil || r.Form.Get("scope") != tsauth.ScopeDocumentsWrite {
					t.Error("permission was replaced")
				}
				w.WriteHeader(status)
				fmt.Fprintf(w, `{"error":%q}`, code)
			}))
			t.Cleanup(issuer.Close)
			source, err := tsauth.NewAPIKeySource(tsauth.APIKeyConfig{Issuer: issuer.URL + "/terrascale", Key: "01ARZ3NDEKTSV4RRFFQ69G5FA8_0123456789abcdefghjkmnpqrstvwxyz0123456789abcdefghjk", Project: testProject, Scopes: []string{tsauth.ScopeDocumentsWrite}})
			if err != nil {
				t.Fatal(err)
			}
			c, err := v1.New(v1.Config{TokenSource: source, HTTPClient: &http.Client{Transport: failTransport{t}}})
			if err != nil {
				t.Fatal(err)
			}
			_, err = c.Database(testProject, "main").Collection("users").Get(t.Context(), "01ARZ3NDEKTSV4RRFFQ69G5FD2")
			var oauthErr *tsauth.Error
			if !errors.As(err, &oauthErr) || oauthErr.Code != code || oauthErr.Status != status {
				t.Fatalf("exchange=%v", err)
			}
		})
	}
}

func TestErrorResponsePreservesBodyAndRequestID(t *testing.T) {
	for _, tc := range []struct {
		name, body string
		status     int
		code       v1.ErrorCode
	}{
		{"wrong issuer fixture", `{"error":{"code":"invalid_key","message":"denied"}}`, 401, v1.ErrInvalidKey},
		{"wrong audience fixture", `{"error":{"code":"invalid_key"}}`, 401, v1.ErrInvalidKey},
		{"expired or revoked fixture", `{"error":{"code":"invalid_key"}}`, 401, v1.ErrInvalidKey},
		{"missing permission fixture", `{"error":{"code":"forbidden_scope","op":0,"details":[{"path":"/x"}]}}`, 403, v1.ErrForbiddenScope},
		{"wrong project fixture", `{"error":{"code":"unknown_database"}}`, 404, v1.ErrUnknownDatabase},
		{"malformed", `{"error":`, 400, "http_400"},
		{"empty", "", 403, "http_403"},
		{"plain message", `"denied"`, 403, v1.ErrForbidden},
	} {
		t.Run(tc.name, func(t *testing.T) {
			var hits atomic.Int64
			peer := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				hits.Add(1)
				w.Header().Set("X-Request-ID", "fixture-request")
				w.Header().Set("ETag", `"tombstone"`)
				w.WriteHeader(tc.status)
				fmt.Fprint(w, tc.body)
			}))
			t.Cleanup(peer.Close)
			c, err := v1.New(v1.Config{BaseURL: peer.URL, Token: "fixture.jwt.signature"})
			if err != nil {
				t.Fatal(err)
			}
			_, err = c.Database(testProject, "main").Collection("users").Get(t.Context(), "01ARZ3NDEKTSV4RRFFQ69G5FD2")
			var apiErr *v1.APIError
			if !errors.As(err, &apiErr) || !errors.Is(err, tc.code) || apiErr.Status != tc.status || string(apiErr.Body) != tc.body || apiErr.RequestID != "fixture-request" || apiErr.ETag != "tombstone" || hits.Load() != 1 {
				t.Fatalf("error=%+v hits=%d", apiErr, hits.Load())
			}
		})
	}
}

func TestTypedResponsesRejectEmptyNullAndTrailingValues(t *testing.T) {
	for _, body := range []string{"", " ", "null", `{} ]`, `{} {}`, `{"id":"01ARZ3NDEKTSV4RRFFQ69G5FD2","version":"v","state":"value","document":{}} ]`} {
		t.Run(fmt.Sprintf("%q", body), func(t *testing.T) {
			peer := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { fmt.Fprint(w, body) }))
			t.Cleanup(peer.Close)
			c, err := v1.New(v1.Config{BaseURL: peer.URL, Token: testToken})
			if err != nil {
				t.Fatal(err)
			}
			if _, err := c.Database(testProject, "main").Collection("users").Get(t.Context(), "01ARZ3NDEKTSV4RRFFQ69G5FD2"); err == nil {
				t.Fatal("accepted malformed success body")
			}
		})
	}
	var result any
	if err := v1.Decode([]byte("null"), &result); err != nil || result != nil {
		t.Fatalf("valid JSON null Decode=%v", err)
	}
}

func TestSQLRowsRejectMissingNullOrNonobjectValues(t *testing.T) {
	for _, row := range []string{`{"id":"01ARZ3NDEKTSV4RRFFQ69G5FD2"}`, `{"id":"01ARZ3NDEKTSV4RRFFQ69G5FD2","value":null}`, `{"id":"01ARZ3NDEKTSV4RRFFQ69G5FD2","value":[]}`, `{"id":"01ARZ3NDEKTSV4RRFFQ69G5FD2","value":1}`} {
		t.Run(row, func(t *testing.T) {
			peer := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				fmt.Fprintf(w, `{"matched":1,"truncated":false,"rows":[%s]}`, row)
			}))
			t.Cleanup(peer.Close)
			c, err := v1.New(v1.Config{BaseURL: peer.URL, Token: "fixture.jwt.signature"})
			if err != nil {
				t.Fatal(err)
			}
			if _, err := c.Database(testProject, "main").SQL(t.Context(), "SELECT * FROM users"); err == nil {
				t.Fatal("accepted malformed SQL row")
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
	_, err = c.Database(testProject, "main").Collection("users").Put(ctx, "01ARZ3NDEKTSV4RRFFQ69G5FD2", map[string]any{})
	if !errors.Is(err, context.Canceled) || errors.Is(err, v1.ErrCommitUncertain) {
		t.Fatalf("cancel=%v", err)
	}
}

func TestPUTRetriesOnlyKnownRejectionsWithCAS(t *testing.T) {
	for _, conditional := range []bool{false, true} {
		t.Run(fmt.Sprint(conditional), func(t *testing.T) {
			var hits atomic.Int64
			peer := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				if r.Header.Get("Idempotency-Key") != "" {
					t.Error("sent unsupported idempotency key")
				}
				body, _ := io.ReadAll(r.Body)
				if string(body) != `{"n":1}` || r.URL.Query().Get("id") != "01ARZ3NDEKTSV4RRFFQ69G5FD4" {
					t.Error("write identity or body changed")
				}
				if hits.Add(1) == 1 {
					w.Header().Set("Retry-After", "0")
					w.WriteHeader(http.StatusTooManyRequests)
					fmt.Fprint(w, `{"error":{"code":"rate_limited"}}`)
					return
				}
				fmt.Fprint(w, `{"id":"01ARZ3NDEKTSV4RRFFQ69G5FD4","version":"new","offset":"1","durability":"local_durable"}`)
			}))
			t.Cleanup(peer.Close)
			c, err := v1.New(v1.Config{BaseURL: peer.URL, Token: testToken})
			if err != nil {
				t.Fatal(err)
			}
			var opts []v1.WriteOption
			if conditional {
				opts = []v1.WriteOption{v1.IfMatch("old")}
			}
			_, err = c.Database(testProject, "main").Collection("users").Put(t.Context(), "01ARZ3NDEKTSV4RRFFQ69G5FD4", map[string]any{"n": 1}, opts...)
			if conditional && (err != nil || hits.Load() != 2) || !conditional && (!errors.Is(err, v1.ErrRateLimited) || hits.Load() != 1) {
				t.Fatalf("hits=%d error=%v", hits.Load(), err)
			}
		})
	}
}
