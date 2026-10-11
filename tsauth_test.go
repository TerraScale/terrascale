package sdk

import (
	"errors"
	"io"
	"net/http"
	"net/http/httptest"
	"reflect"
	"strings"
	"testing"
	"time"
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
	instance := newTSAuthTestInstance(t, Config{HTTPClient: &http.Client{Transport: transport}, MaxRetryAttempts: 1, RetryDelay: time.Millisecond})
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

func TestTSAuthRetryPolicy(t *testing.T) {
	var gets, posts int
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		switch {
		case r.URL.Path == "/acme/oauth/userinfo" && r.Method == http.MethodGet:
			gets++
			if gets == 1 {
				w.WriteHeader(http.StatusServiceUnavailable)
				return
			}
			_, _ = w.Write([]byte(`{"sub":"01ARZ3NDEKTSV4RRFFQ69G5FA4"}`))
		case r.URL.Path == "/acme/oauth/revoke" && r.Method == http.MethodPost:
			posts++
			w.WriteHeader(http.StatusServiceUnavailable)
			_, _ = w.Write([]byte(`{"error":{"code":"server_error","message":"unavailable"}}`))
		default:
			http.NotFound(w, r)
		}
	}))
	defer server.Close()

	instance := newTSAuthTestInstance(t, Config{
		TSAuthBaseURL:    server.URL,
		MaxRetryAttempts: 1,
		RetryDelay:       time.Millisecond,
	})
	if _, err := instance.OIDC.GetUserInfo(t.Context(), "access-token"); err != nil {
		t.Fatalf("retried userinfo error = %v", err)
	}
	if gets != 2 {
		t.Fatalf("get attempts = %d, want 2", gets)
	}
	err := instance.OIDC.RevokeToken(t.Context(), TSAuthOAuthClientAuthentication{ClientID: "cli_1", ClientSecret: "csek", Method: TSAuthClientSecretPost}, "at_1")
	var apiErr *APIError
	if !errors.As(err, &apiErr) || apiErr.Status != http.StatusServiceUnavailable {
		t.Fatalf("post error = %+v", err)
	}
	if posts != 1 {
		t.Fatalf("post attempts = %d, want 1", posts)
	}
}

func TestTSAuthPreflightValidation(t *testing.T) {
	unwired := &TSAuthClient{c: &Client{cfg: Config{TSAuthBaseURL: "http://127.0.0.1:1"}}}
	if _, err := unwired.Instance(""); err == nil {
		t.Fatal("blank slug accepted")
	}
	if _, err := unwired.Instance("has space"); err == nil {
		t.Fatal("slug with space accepted")
	}
	if _, err := unwired.Instance("a/b"); err == nil {
		t.Fatal("slug with slash accepted")
	}
	var validationErr *ValidationError
	if _, err := unwired.Instance("\ta"); !errors.As(err, &validationErr) {
		t.Fatalf("slug error = %v, want *ValidationError", err)
	}

	instance, err := unwired.Instance("acme")
	if err != nil {
		t.Fatal(err)
	}
	checks := []error{
		func() error {
			_, err := instance.OIDC.ExchangeAuthorizationCode(t.Context(), TSAuthOAuthClientAuthentication{ClientID: "cli", ClientSecret: "sec", Method: TSAuthClientSecretPost}, "", "https://app.example/cb", "")
			return err
		}(),
		func() error { _, err := instance.OIDC.GetUserInfo(t.Context(), " "); return err }(),
	}
	for _, err := range checks {
		if err == nil {
			t.Fatal("invalid input accepted")
		}
		if !errors.As(err, &validationErr) {
			t.Fatalf("error = %v (%T), want *ValidationError", err, err)
		}
	}
}

func TestTSAuthOIDC(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		switch r.URL.Path {
		case "/acme/.well-known/openid-configuration":
			if got := r.Header.Get("Authorization"); got != "" {
				t.Errorf("discovery authorization = %q", got)
			}
			_, _ = w.Write([]byte(`{"issuer":"https://auth.example.com/acme","authorization_endpoint":"https://auth.example.com/acme/oauth/authorize","token_endpoint":"https://auth.example.com/acme/oauth/token","userinfo_endpoint":"https://auth.example.com/acme/oauth/userinfo","jwks_uri":"https://auth.example.com/acme/.well-known/jwks.json","revocation_endpoint":"https://auth.example.com/acme/oauth/revoke","end_session_endpoint":"https://auth.example.com/acme/oauth/logout","response_types_supported":["code"],"grant_types_supported":["authorization_code","refresh_token","client_credentials"],"code_challenge_methods_supported":["S256"],"subject_types_supported":["public"],"id_token_signing_alg_values_supported":["ES256"],"token_endpoint_auth_methods_supported":["client_secret_post","none"],"scopes_supported":["openid","email","profile"]}`))
		case "/acme/.well-known/jwks.json":
			if got := r.Header.Get("Authorization"); got != "" {
				t.Errorf("jwks authorization = %q", got)
			}
			_, _ = w.Write([]byte(`{"keys":[{"kty":"EC","crv":"P-256","alg":"ES256","use":"sig","kid":"key-1","x":"x","y":"y"}]}`))
		case "/acme/oauth/token":
			if got := r.Header.Get("Content-Type"); got != "application/x-www-form-urlencoded" {
				t.Errorf("token content type = %q", got)
			}
			if got := r.Header.Get("Authorization"); got != "" {
				t.Errorf("token authorization = %q", got)
			}
			if err := r.ParseForm(); err != nil {
				t.Errorf("token form parse error = %v", err)
			}
			if got := r.PostForm.Get("client_id"); got != "cli_1" {
				t.Errorf("client_id = %q", got)
			}
			if got := r.PostForm.Get("client_secret"); got != "csek" {
				t.Errorf("client_secret = %q", got)
			}
			switch r.PostForm.Get("grant_type") {
			case "authorization_code":
				if got := r.PostForm.Get("code"); got != "code_1" {
					t.Errorf("code = %q", got)
				}
				if got := r.PostForm.Get("redirect_uri"); got != "https://app.example/cb" {
					t.Errorf("redirect_uri = %q", got)
				}
				if got := r.PostForm.Get("code_verifier"); got != "verifier_123" {
					t.Errorf("code_verifier = %q", got)
				}
			case "refresh_token":
				if got := r.PostForm.Get("refresh_token"); got != "rt_1" {
					t.Errorf("refresh_token = %q", got)
				}
			case "client_credentials":
				if got := r.PostForm.Get("scope"); got != "openid profile" {
					t.Errorf("scope = %q", got)
				}
			default:
				t.Errorf("grant_type = %q", r.PostForm.Get("grant_type"))
			}
			if r.PostForm.Get("grant_type") == "client_credentials" {
				_, _ = w.Write([]byte(`{"access_token":"at_1","token_type":"Bearer","expires_in":3600,"scope":"openid profile"}`))
				return
			}
			_, _ = w.Write([]byte(`{"access_token":"at_1","token_type":"Bearer","expires_in":3600,"scope":"openid profile","refresh_token":"rt_1","id_token":"id_1"}`))
		case "/acme/oauth/revoke":
			if err := r.ParseForm(); err != nil {
				t.Errorf("revoke form parse error = %v", err)
			}
			if got := r.PostForm.Get("token"); got != "at_1" {
				t.Errorf("revoke token = %q", got)
			}
			// The service answers 200 with an empty body (RFC 7009).
			w.WriteHeader(http.StatusOK)
		case "/acme/oauth/userinfo":
			if got := r.Header.Get("Authorization"); got != "Bearer at_live" {
				t.Errorf("userinfo authorization = %q", got)
			}
			_, _ = w.Write([]byte(`{"sub":"01ARZ3NDEKTSV4RRFFQ69G5FA4","email":"ada@example.com","email_verified":true,"name":"Ada"}`))
		default:
			http.NotFound(w, r)
		}
	}))
	defer server.Close()

	oidc := newTSAuthTestInstance(t, Config{TSAuthBaseURL: server.URL}).OIDC

	doc, err := oidc.GetDiscoveryDocument(t.Context())
	if err != nil || doc.Issuer != "https://auth.example.com/acme" || doc.JwksURI != "https://auth.example.com/acme/.well-known/jwks.json" || len(doc.GrantTypesSupported) != 3 {
		t.Fatalf("discovery = %+v, error = %v", doc, err)
	}
	jwks, err := oidc.GetJwks(t.Context())
	if err != nil || len(jwks.Keys) != 1 || jwks.Keys[0].Kid != "key-1" || jwks.Keys[0].Crv != "P-256" {
		t.Fatalf("jwks = %+v, error = %v", jwks, err)
	}
	code, err := oidc.ExchangeAuthorizationCode(t.Context(), TSAuthOAuthClientAuthentication{ClientID: "cli_1", ClientSecret: "csek", Method: TSAuthClientSecretPost}, "code_1", "https://app.example/cb", "verifier_123")
	if err != nil || code.AccessToken != "at_1" || code.ExpiresIn != 3600 || code.RefreshToken == nil || *code.RefreshToken != "rt_1" || code.IDToken == nil || *code.IDToken != "id_1" {
		t.Fatalf("code exchange = %+v, error = %v", code, err)
	}
	refreshed, err := oidc.RefreshToken(t.Context(), TSAuthOAuthClientAuthentication{ClientID: "cli_1", ClientSecret: "csek", Method: TSAuthClientSecretPost}, "rt_1")
	if err != nil || refreshed.AccessToken != "at_1" {
		t.Fatalf("refresh = %+v, error = %v", refreshed, err)
	}
	credentials, err := oidc.RequestClientCredentialsToken(t.Context(), TSAuthOAuthClientAuthentication{ClientID: "cli_1", ClientSecret: "csek", Method: TSAuthClientSecretPost}, "openid profile")
	if err != nil || credentials.RefreshToken != nil {
		t.Fatalf("client credentials = %+v, error = %v", credentials, err)
	}
	if err := oidc.RevokeToken(t.Context(), TSAuthOAuthClientAuthentication{ClientID: "cli_1", ClientSecret: "csek", Method: TSAuthClientSecretPost}, "at_1"); err != nil {
		t.Fatalf("revoke error = %v", err)
	}
	userInfo, err := oidc.GetUserInfo(t.Context(), "at_live")
	if err != nil || userInfo.Sub != "01ARZ3NDEKTSV4RRFFQ69G5FA4" || userInfo.Email == nil || userInfo.EmailVerified == nil || !*userInfo.EmailVerified {
		t.Fatalf("userinfo = %+v, error = %v", userInfo, err)
	}
}
