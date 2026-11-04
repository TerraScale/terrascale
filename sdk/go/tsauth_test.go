package sdk

import (
	"errors"
	"io"
	"net/http"
	"net/http/httptest"
	"reflect"
	"strings"
	"testing"
)

func newTSAuthTestInstance(t *testing.T, cfg Config) *TSAuthInstanceClient {
	t.Helper()
	client, err := NewClient(cfg)
	if err != nil {
		t.Fatal(err)
	}
	instance, err := client.TSAuth.Instance("acme")
	if err != nil {
		t.Fatal(err)
	}
	return instance
}

// Auth calls go to the one global host. A 503 is retried there, never failed
// over to another origin.
func TestTSAuthUsesOneGlobalHost(t *testing.T) {
	var hosts []string
	transport := tsAuthEdgeTransport(func(r *http.Request) (*http.Response, error) {
		hosts = append(hosts, r.URL.Hostname())
		status := http.StatusOK
		if len(hosts) == 1 {
			status = http.StatusServiceUnavailable
		}
		return &http.Response{StatusCode: status, Header: http.Header{"Content-Type": []string{"application/json"}}, Body: io.NopCloser(strings.NewReader(`{"sub":"01ARZ3NDEKTSV4RRFFQ69G5FA4"}`)), Request: r}, nil
	})
	instance := newTSAuthTestInstance(t, Config{HTTPClient: &http.Client{Transport: transport}})
	if _, err := instance.OIDC.GetUserInfo(t.Context(), "access-token"); err != nil {
		t.Fatal(err)
	}
	if !reflect.DeepEqual(hosts, []string{"auth.terrascale.tech", "auth.terrascale.tech"}) {
		t.Fatalf("hosts = %v", hosts)
	}
}

func TestTSAuthNeverForwardsCredentialsOnRedirect(t *testing.T) {
	forwarded := false
	target := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		forwarded = true
		w.WriteHeader(http.StatusOK)
	}))
	defer target.Close()
	origin := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if got := r.Header.Get("Authorization"); got != "Bearer access-token" {
			t.Errorf("authorization = %q", got)
		}
		http.Redirect(w, r, target.URL, http.StatusTemporaryRedirect)
	}))
	defer origin.Close()

	instance := newTSAuthTestInstance(t, Config{TSAuthBaseURL: origin.URL})
	if _, err := instance.OIDC.GetUserInfo(t.Context(), "access-token"); err == nil {
		t.Fatal("redirect unexpectedly succeeded")
	}
	if forwarded {
		t.Fatal("auth request followed a redirect")
	}
}

func TestTSAuthErrorShapes(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		switch r.URL.Path {
		case "/acme/oauth/userinfo":
			w.WriteHeader(http.StatusBadRequest)
			_, _ = w.Write([]byte(`{"error":{"code":"invalid_request","message":"the request is invalid"}}`))
		case "/acme/oauth/token":
			w.WriteHeader(http.StatusUnauthorized)
			_, _ = w.Write([]byte(`{"error":"invalid_client","error_description":"client authentication failed"}`))
		default:
			http.NotFound(w, r)
		}
	}))
	defer server.Close()

	instance := newTSAuthTestInstance(t, Config{TSAuthBaseURL: server.URL})
	_, err := instance.OIDC.GetUserInfo(t.Context(), "access-token")
	var apiErr *APIError
	if !errors.As(err, &apiErr) || apiErr.Status != http.StatusBadRequest || apiErr.Code != "invalid_request" || apiErr.Message != "the request is invalid" {
		t.Fatalf("nested envelope error = %+v", err)
	}
	_, err = instance.OIDC.ExchangeAuthorizationCode(t.Context(), TSAuthOAuthClientAuthentication{ClientID: "cli_1", ClientSecret: "csek", Method: TSAuthClientSecretPost}, "code_1", "https://app.example/cb", "")
	if !errors.As(err, &apiErr) || apiErr.Status != http.StatusUnauthorized || apiErr.Code != "invalid_client" || apiErr.Message != "client authentication failed" {
		t.Fatalf("flat oauth error = %+v", err)
	}
}

// The service answers 200 with an empty body (RFC 7009).
