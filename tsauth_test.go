package sdk

import (
	"crypto/hmac"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
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

func tsAuthFullUser() map[string]any {
	return map[string]any{
		"id":               "user-1",
		"email":            "ada@example.com",
		"email_verified":   true,
		"username":         "ada",
		"name":             "Ada",
		"banned":           false,
		"ban_reason":       "spam",
		"created_at":       1700000000,
		"public_metadata":  map[string]any{},
		"private_metadata": map[string]any{"risk": "low"},
	}
}

func TestTSAuthPublicOriginDiscoversRegionsAndProxyOptIn(t *testing.T) {
	var hosts []string
	transport := tsAuthEdgeTransport(func(r *http.Request) (*http.Response, error) {
		hosts = append(hosts, r.URL.Hostname())
		body := `{"data":[]}`
		status := http.StatusOK
		switch r.URL.Hostname() {
		case "atlas.terrascale.tech":
			if r.Header.Get("Authorization") != "" || r.URL.Path != "/api/v1/auth/discovery" {
				t.Errorf("metadata request leaked credentials or used wrong path: %s", r.URL)
			}
			body = `{"v":1,"refreshSeconds":60,"endpoints":[{"region":"SFO","url":"https://sfo.auth.terrascale.tech"},{"region":"GRU","url":"https://gru.auth.terrascale.tech"}]}`
		case "sfo.auth.terrascale.tech":
			if r.Header.Get("Authorization") != "Bearer sk_test_key" {
				t.Errorf("regional request authorization = %q", r.Header.Get("Authorization"))
			}
			status = http.StatusServiceUnavailable
		case "gru.auth.terrascale.tech":
			if r.Header.Get("Authorization") != "Bearer sk_test_key" {
				t.Errorf("regional request authorization = %q", r.Header.Get("Authorization"))
			}
		case "auth.terrascale.tech":
		default:
			t.Errorf("unexpected host: %s", r.URL.Hostname())
		}
		return &http.Response{StatusCode: status, Header: http.Header{"Content-Type": []string{"application/json"}}, Body: io.NopCloser(strings.NewReader(body)), Request: r}, nil
	})
	config := Config{TSAuthBaseURL: publicAuthBaseURL, AccessToken: "sk_test_key", HTTPClient: &http.Client{Transport: transport}}
	instance := newTSAuthTestInstance(t, config)
	if _, err := instance.Users.List(t.Context(), 0, ""); err != nil {
		t.Fatal(err)
	}
	if !reflect.DeepEqual(hosts, []string{"atlas.terrascale.tech", "sfo.auth.terrascale.tech", "gru.auth.terrascale.tech"}) {
		t.Fatalf("direct hosts = %v", hosts)
	}
	hosts = nil
	if _, err := instance.Users.Create(t.Context(), CreateTSAuthUserRequest{Email: "ada@example.com"}); err == nil {
		t.Fatal("mutation unexpectedly succeeded")
	}
	if !reflect.DeepEqual(hosts, []string{"sfo.auth.terrascale.tech"}) {
		t.Fatalf("mutation hosts = %v", hosts)
	}
	hosts = nil
	config.TSAuthUseReverseProxy = true
	proxy := newTSAuthTestInstance(t, config)
	if _, err := proxy.Users.List(t.Context(), 0, ""); err != nil {
		t.Fatal(err)
	}
	if !reflect.DeepEqual(hosts, []string{"auth.terrascale.tech"}) {
		t.Fatalf("proxy hosts = %v", hosts)
	}
}

func TestTSAuthNeverForwardsManagementCredentialsOnRedirect(t *testing.T) {
	forwarded := false
	target := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		forwarded = true
		w.WriteHeader(http.StatusOK)
	}))
	defer target.Close()
	origin := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if got := r.Header.Get("Authorization"); got != "Bearer sk_test_key" {
			t.Errorf("authorization = %q", got)
		}
		http.Redirect(w, r, target.URL, http.StatusTemporaryRedirect)
	}))
	defer origin.Close()

	instance := newTSAuthTestInstance(t, Config{TSAuthBaseURL: origin.URL, AccessToken: "sk_test_key"})
	if _, err := instance.Users.List(t.Context(), 0, ""); err == nil {
		t.Fatal("redirect unexpectedly succeeded")
	}
	if forwarded {
		t.Fatal("auth request followed a redirect")
	}
}

func TestTSAuthUserJourney(t *testing.T) {
	var requests []string
	var patchBodies []map[string]any
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if got := r.Header.Get("Authorization"); got != "Bearer sk_test_key" {
			t.Errorf("authorization = %q", got)
		}
		requests = append(requests, r.Method+" "+r.URL.RequestURI())
		w.Header().Set("Content-Type", "application/json")
		switch {
		case r.URL.Path == "/v1/acme/users" && r.Method == http.MethodPost:
			var body CreateTSAuthUserRequest
			if err := json.NewDecoder(r.Body).Decode(&body); err != nil || body.Email != "ada@example.com" || body.Password != "hunter22!" {
				t.Errorf("create body = %+v, error = %v", body, err)
			}
			_ = json.NewEncoder(w).Encode(tsAuthFullUser())
		case r.URL.Path == "/v1/acme/users" && r.Method == http.MethodGet:
			switch r.URL.Query().Get("starting_after") {
			case "":
				data := []map[string]any{
					{"id": "user-1", "email": "ada@example.com", "email_verified": true, "username": "ada", "name": "Ada", "banned": false, "created_at": 1700000000, "public_metadata": map[string]any{}},
					{"id": "user-2", "email": "grace@example.com", "email_verified": false, "username": "grace", "name": "Grace", "banned": false, "created_at": 1700000001, "public_metadata": map[string]any{}},
				}
				_ = json.NewEncoder(w).Encode(map[string]any{"data": data})
			case "user-2":
				_ = json.NewEncoder(w).Encode(map[string]any{"data": []map[string]any{{"id": "user-3", "email": "linus@example.com", "email_verified": false, "username": "linus", "name": "Linus", "banned": false, "created_at": 1700000002, "public_metadata": map[string]any{}}}})
			default:
				_ = json.NewEncoder(w).Encode(map[string]any{"data": []map[string]any{}})
			}
		case r.URL.Path == "/v1/acme/users/user-1" && r.Method == http.MethodGet:
			_ = json.NewEncoder(w).Encode(tsAuthFullUser())
		case r.URL.Path == "/v1/acme/users/user-1" && r.Method == http.MethodPatch:
			var body map[string]any
			if err := json.NewDecoder(r.Body).Decode(&body); err != nil {
				t.Errorf("patch body decode error = %v", err)
			}
			patchBodies = append(patchBodies, body)
			_ = json.NewEncoder(w).Encode(tsAuthFullUser())
		case r.URL.Path == "/v1/acme/users/user-1/impersonate" && r.Method == http.MethodPost:
			if r.ContentLength > 0 {
				t.Errorf("impersonate body length = %d", r.ContentLength)
			}
			_ = json.NewEncoder(w).Encode(map[string]any{"token": "imp_tok", "expires_at": 1893456000})
		case r.URL.Path == "/v1/acme/users/user-1/sessions" && r.Method == http.MethodGet:
			_ = json.NewEncoder(w).Encode(map[string]any{"data": []map[string]any{{"id": "sess-1", "status": "active", "created_at": 1700000000, "expires_at": 1700003600}}})
		case r.URL.Path == "/v1/acme/users/user-1/sessions/revoke-all" && r.Method == http.MethodPost:
			w.WriteHeader(http.StatusNoContent)
		case r.URL.Path == "/v1/acme/users/user-1" && r.Method == http.MethodDelete:
			w.WriteHeader(http.StatusNoContent)
		default:
			http.NotFound(w, r)
		}
	}))
	defer server.Close()

	instance := newTSAuthTestInstance(t, Config{APIBaseURL: server.URL, AccessToken: "sk_test_key"})
	users := instance.Users

	created, err := users.Create(t.Context(), CreateTSAuthUserRequest{Email: "ada@example.com", Username: "ada", Password: "hunter22!", Name: "Ada"})
	if err != nil || created.ID != "user-1" || created.BanReason == nil || *created.BanReason != "spam" || created.PrivateMetadata["risk"] != "low" {
		t.Fatalf("create = %+v, error = %v", created, err)
	}
	page, err := users.List(t.Context(), 2, "")
	if err != nil || len(page.Data) != 2 || page.Data[1].ID != "user-2" || page.Data[1].BanReason != nil {
		t.Fatalf("list = %+v, error = %v", page, err)
	}
	// The second ListAll page (1 user) is shorter than the requested page
	// size, so the walk stops without a confirming empty page.
	all, err := users.ListAll(t.Context(), 2)
	if err != nil || len(all) != 3 || all[len(all)-1].ID != "user-3" {
		t.Fatalf("list all = %+v, error = %v", all, err)
	}
	got, err := users.Get(t.Context(), "user-1")
	if err != nil || got.PrivateMetadata == nil {
		t.Fatalf("get = %+v, error = %v", got, err)
	}
	updated, err := users.Update(t.Context(), "user-1",
		TSAuthUserPatch{}.WithName("Ada Lovelace").UnsetUsername().WithPublicMetadata(map[string]any{"tier": "gold"}))
	if err != nil || updated.ID != "user-1" {
		t.Fatalf("update = %+v, error = %v", updated, err)
	}
	impersonation, err := users.Impersonate(t.Context(), "user-1")
	if err != nil || impersonation.Token != "imp_tok" || impersonation.ExpiresAt != 1893456000 {
		t.Fatalf("impersonate = %+v, error = %v", impersonation, err)
	}
	sessions, err := users.ListSessions(t.Context(), "user-1")
	if err != nil || len(sessions) != 1 || sessions[0].ID != "sess-1" {
		t.Fatalf("sessions = %+v, error = %v", sessions, err)
	}
	if err := users.RevokeAllSessions(t.Context(), "user-1"); err != nil {
		t.Fatalf("revoke all error = %v", err)
	}
	if err := users.Delete(t.Context(), "user-1"); err != nil {
		t.Fatalf("delete error = %v", err)
	}

	wantPatch := map[string]any{"name": "Ada Lovelace", "username": nil, "public_metadata": map[string]any{"tier": "gold"}}
	if !reflect.DeepEqual(patchBodies, []map[string]any{wantPatch}) {
		t.Fatalf("patch bodies = %#v, want %#v", patchBodies, wantPatch)
	}
	want := []string{
		"POST /v1/acme/users",
		"GET /v1/acme/users?limit=2",
		"GET /v1/acme/users?limit=2",
		"GET /v1/acme/users?limit=2&starting_after=user-2",
		"GET /v1/acme/users/user-1",
		"PATCH /v1/acme/users/user-1",
		"POST /v1/acme/users/user-1/impersonate",
		"GET /v1/acme/users/user-1/sessions",
		"POST /v1/acme/users/user-1/sessions/revoke-all",
		"DELETE /v1/acme/users/user-1",
	}
	if !reflect.DeepEqual(requests, want) {
		t.Fatalf("requests = %#v, want %#v", requests, want)
	}
}

func TestTSAuthErrorShapes(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		switch r.URL.Path {
		case "/v1/acme/instance":
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

	instance := newTSAuthTestInstance(t, Config{APIBaseURL: server.URL, AccessToken: "sk_test_key"})
	_, err := instance.Instance.Get(t.Context())
	var apiErr *APIError
	if !errors.As(err, &apiErr) || apiErr.Status != http.StatusBadRequest || apiErr.Code != "invalid_request" || apiErr.Message != "the request is invalid" {
		t.Fatalf("nested envelope error = %+v", err)
	}
	_, err = instance.OIDC.ExchangeAuthorizationCode(t.Context(), "cli_1", "csek", "code_1", "https://app.example/cb", "")
	if !errors.As(err, &apiErr) || apiErr.Status != http.StatusUnauthorized || apiErr.Code != "invalid_client" || apiErr.Message != "client authentication failed" {
		t.Fatalf("flat oauth error = %+v", err)
	}
}

func TestTSAuthRetryPolicy(t *testing.T) {
	var gets, posts int
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		switch {
		case r.URL.Path == "/v1/acme/users" && r.Method == http.MethodGet:
			gets++
			if gets == 1 {
				w.WriteHeader(http.StatusServiceUnavailable)
				return
			}
			_, _ = w.Write([]byte(`{"data":[]}`))
		case r.URL.Path == "/v1/acme/users" && r.Method == http.MethodPost:
			posts++
			w.WriteHeader(http.StatusServiceUnavailable)
			_, _ = w.Write([]byte(`{"error":{"code":"server_error","message":"unavailable"}}`))
		default:
			http.NotFound(w, r)
		}
	}))
	defer server.Close()

	instance := newTSAuthTestInstance(t, Config{
		APIBaseURL:       server.URL,
		AccessToken:      "sk_test_key",
		MaxRetryAttempts: 1,
		RetryDelay:       time.Millisecond,
	})
	if _, err := instance.Users.List(t.Context(), 0, ""); err != nil {
		t.Fatalf("retried list error = %v", err)
	}
	if gets != 2 {
		t.Fatalf("get attempts = %d, want 2", gets)
	}
	_, err := instance.Users.Create(t.Context(), CreateTSAuthUserRequest{Email: "ada@example.com"})
	var apiErr *APIError
	if !errors.As(err, &apiErr) || apiErr.Status != http.StatusServiceUnavailable {
		t.Fatalf("post error = %+v", err)
	}
	if posts != 1 {
		t.Fatalf("post attempts = %d, want 1", posts)
	}
}

func TestTSAuthOneTimeSecretsAndCasing(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		switch {
		case r.URL.Path == "/v1/acme/api-keys" && r.Method == http.MethodPost:
			var body CreateTSAuthAPIKeyRequest
			if err := json.NewDecoder(r.Body).Decode(&body); err != nil || body.Name != "ops" || len(body.Scopes) != 1 {
				t.Errorf("api-key create body = %+v, error = %v", body, err)
			}
			_, _ = w.Write([]byte(`{"id":"key_1","name":"ops","api_key":"sk_once","scopes":["users:read"]}`))
		case r.URL.Path == "/v1/acme/api-keys" && r.Method == http.MethodGet:
			_, _ = w.Write([]byte(`{"data":[{"id":"key_1","name":"ops","scopes":["users:read"],"created_at":1,"revoked":false}]}`))
		case r.URL.Path == "/v1/acme/oauth-clients" && r.Method == http.MethodPost:
			var body CreateTSAuthOAuthClientRequest
			if err := json.NewDecoder(r.Body).Decode(&body); err != nil || body.Name != "web" || len(body.RedirectURIs) != 1 {
				t.Errorf("oauth-client create body = %+v, error = %v", body, err)
			}
			_, _ = w.Write([]byte(`{"client":{"id":"clt_1","clientId":"cli_1","name":"web","redirectUris":["https://app.example/cb"],"tokenEndpointAuthMethod":"client_secret_post","public":false,"createdAt":1700000000},"client_secret":"csek_once"}`))
		case r.URL.Path == "/v1/acme/oauth-clients" && r.Method == http.MethodGet:
			_, _ = w.Write([]byte(`{"clients":[{"id":"clt_1","clientId":"cli_1","name":"web","redirectUris":["https://app.example/cb"],"tokenEndpointAuthMethod":"client_secret_post","public":false,"createdAt":1700000000}]}`))
		case r.URL.Path == "/v1/acme/webhooks" && r.Method == http.MethodPost:
			_, _ = w.Write([]byte(`{"id":"wh_1","url":"https://hooks.example/cb","events":["user.created","user.*"],"enabled":true,"created_at":1,"signing_secret":"whsec_12345678901234567890123456789012"}`))
		case r.URL.Path == "/v1/acme/webhooks" && r.Method == http.MethodGet:
			_, _ = w.Write([]byte(`[{"id":"wh_1","url":"https://hooks.example/cb","events":["user.created","user.*"],"enabled":true,"created_at":1}]`))
		default:
			http.NotFound(w, r)
		}
	}))
	defer server.Close()

	instance := newTSAuthTestInstance(t, Config{APIBaseURL: server.URL, AccessToken: "sk_test_key"})

	key, err := instance.APIKeys.Create(t.Context(), CreateTSAuthAPIKeyRequest{Name: "ops", Scopes: []string{"users:read"}})
	if err != nil || key.APIKey != "sk_once" {
		t.Fatalf("api key create = %+v, error = %v", key, err)
	}
	keys, err := instance.APIKeys.List(t.Context())
	if err != nil || len(keys) != 1 || keys[0].APIKey != "" || keys[0].CreatedAt != 1 {
		t.Fatalf("api key list = %+v, error = %v", keys, err)
	}
	client, err := instance.OAuthClients.Create(t.Context(), CreateTSAuthOAuthClientRequest{Name: "web", RedirectURIs: []string{"https://app.example/cb"}})
	if err != nil || client.Client.ID != "clt_1" || client.Client.ClientID != "cli_1" || client.Client.TokenEndpointAuthMethod != "client_secret_post" || client.ClientSecret == nil || *client.ClientSecret != "csek_once" {
		t.Fatalf("oauth client create = %+v, error = %v", client, err)
	}
	clients, err := instance.OAuthClients.List(t.Context())
	if err != nil || len(clients) != 1 || clients[0].ClientID != "cli_1" || clients[0].RedirectURIs[0] != "https://app.example/cb" {
		t.Fatalf("oauth client list = %+v, error = %v", clients, err)
	}
	webhook, err := instance.Webhooks.Create(t.Context(), CreateTSAuthWebhookRequest{URL: "https://hooks.example/cb", Events: []string{"user.created", "user.*"}})
	if err != nil || webhook.SigningSecret != "whsec_12345678901234567890123456789012" {
		t.Fatalf("webhook create = %+v, error = %v", webhook, err)
	}
	webhooks, err := instance.Webhooks.List(t.Context())
	if err != nil || len(webhooks) != 1 || webhooks[0].SigningSecret != "" {
		t.Fatalf("webhook list = %+v, error = %v", webhooks, err)
	}
}

func TestVerifyTSAuthWebhook(t *testing.T) {
	secret := "whsec_12345678901234567890123456789012"
	payload := []byte(`{"id":"evt_1","type":"user.created","created_at":1,"data":{"user":{"id":"user-1"}}}`)
	timestamp := "1700000000"

	signFor := func(key, timestamp string, payload []byte) string {
		mac := hmac.New(sha256.New, []byte(key))
		mac.Write([]byte(timestamp + "." + string(payload)))
		return "sha256=" + hex.EncodeToString(mac.Sum(nil))
	}
	signature := signFor(secret, timestamp, payload)

	if !VerifyTSAuthWebhook(secret, timestamp, signature, payload) {
		t.Fatal("valid signature rejected")
	}
	if VerifyTSAuthWebhook(secret, timestamp, signature, append(payload, '!')) {
		t.Fatal("tampered payload accepted")
	}
	if VerifyTSAuthWebhook("whsec_wrong", timestamp, signature, payload) {
		t.Fatal("wrong secret accepted")
	}
	if VerifyTSAuthWebhook(secret, timestamp, strings.ToUpper(signature), payload) {
		t.Fatal("uppercase hex accepted")
	}
	if VerifyTSAuthWebhook("", timestamp, signature, payload) || VerifyTSAuthWebhook(secret, "", signature, payload) || VerifyTSAuthWebhook(secret, timestamp, "", payload) {
		t.Fatal("malformed inputs accepted")
	}

	now := time.Unix(1700000000, 0)
	fresh := "1700000010"
	if !VerifyTSAuthWebhookWithTolerance(secret, fresh, signFor(secret, fresh, payload), payload, time.Minute, now) {
		t.Fatal("fresh timestamp rejected")
	}
	stale := "1699990000"
	if VerifyTSAuthWebhookWithTolerance(secret, stale, signFor(secret, stale, payload), payload, time.Minute, now) {
		t.Fatal("tolerance-exceeded timestamp accepted")
	}
	if VerifyTSAuthWebhookWithTolerance(secret, "not-a-number", signature, payload, time.Minute, now) {
		t.Fatal("malformed timestamp accepted under tolerance")
	}
}

func TestTSAuthPreflightValidation(t *testing.T) {
	unwired := &TSAuthClient{c: &Client{cfg: Config{APIBaseURL: "http://127.0.0.1:1"}}}
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
		func() error { _, err := instance.Users.Get(t.Context(), ""); return err }(),
		func() error { _, err := instance.Users.Update(t.Context(), "u1", TSAuthUserPatch{}); return err }(),
		func() error { return instance.Users.DeleteMFAFactor(t.Context(), "u1", "") }(),
		func() error { return instance.Sessions.Revoke(t.Context(), " ") }(),
		func() error { _, err := instance.APIKeys.Create(t.Context(), CreateTSAuthAPIKeyRequest{}); return err }(),
		func() error {
			_, err := instance.OAuthClients.Create(t.Context(), CreateTSAuthOAuthClientRequest{Name: "web"})
			return err
		}(),
		func() error { return instance.OAuthClients.Delete(t.Context(), "") }(),
		func() error {
			_, err := instance.Organizations.CreateInvitation(t.Context(), "org_1", "", "dev")
			return err
		}(),
		func() error {
			_, err := instance.Connections.CreateOIDC(t.Context(), CreateTSAuthOIDCConnectionRequest{Name: "Google", IssuerURL: "ftp://example.com", ClientID: "cli", ClientSecret: "sec"})
			return err
		}(),
		func() error { _, err := instance.Directories.RotateToken(t.Context(), ""); return err }(),
		func() error {
			_, err := instance.JWTTemplates.Create(t.Context(), CreateTSAuthJWTTemplateRequest{Name: "session"})
			return err
		}(),
		func() error { _, err := instance.JWTTemplates.MintToken(t.Context(), "bad name!", "u1"); return err }(),
		func() error {
			_, err := instance.Webhooks.Create(t.Context(), CreateTSAuthWebhookRequest{URL: "http://hooks.example/cb", Events: []string{"user.created"}})
			return err
		}(),
		func() error { _, err := instance.Webhooks.ReplayDeadDelivery(t.Context(), ""); return err }(),
		func() error { _, err := instance.Billing.CreateCheckout(t.Context(), " "); return err }(),
		func() error { _, err := instance.Billing.EnforceQuota(t.Context(), -1); return err }(),
		func() error {
			_, err := instance.OIDC.ExchangeAuthorizationCode(t.Context(), "cli", "sec", "", "https://app.example/cb", "")
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
			_, _ = w.Write([]byte(`{"issuer":"https://auth.example.com/acme","authorization_endpoint":"https://auth.example.com/acme/oauth/authorize","token_endpoint":"https://auth.example.com/acme/oauth/token","userinfo_endpoint":"https://auth.example.com/acme/oauth/userinfo","jwks_uri":"https://auth.example.com/acme/.well-known/jwks.json","revocation_endpoint":"https://auth.example.com/acme/oauth/revoke","introspection_endpoint":"https://auth.example.com/acme/oauth/introspect","end_session_endpoint":"https://auth.example.com/acme/oauth/end-session","response_types_supported":["code"],"grant_types_supported":["authorization_code","refresh_token","client_credentials"],"code_challenge_methods_supported":["S256"],"subject_types_supported":["public"],"id_token_signing_alg_values_supported":["ES256"],"token_endpoint_auth_methods_supported":["client_secret_post","none"],"scopes_supported":["openid","email","profile"]}`))
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
		case "/acme/oauth/introspect":
			if err := r.ParseForm(); err != nil {
				t.Errorf("introspect form parse error = %v", err)
			}
			if got := r.PostForm.Get("token"); got != "at_1" {
				t.Errorf("introspect token = %q", got)
			}
			_, _ = w.Write([]byte(`{"active":true,"token_type":"Bearer","iss":"https://auth.example.com/acme","sub":"user-1","client_id":"cli_1","scope":"openid","exp":1700003600,"iat":1700000000}`))
		case "/acme/oauth/userinfo":
			if got := r.Header.Get("Authorization"); got != "Bearer at_live" {
				t.Errorf("userinfo authorization = %q", got)
			}
			_, _ = w.Write([]byte(`{"sub":"user-1","email":"ada@example.com","email_verified":true,"name":"Ada"}`))
		default:
			http.NotFound(w, r)
		}
	}))
	defer server.Close()

	oidc := newTSAuthTestInstance(t, Config{APIBaseURL: server.URL, AccessToken: "sk_test_key"}).OIDC

	doc, err := oidc.GetDiscoveryDocument(t.Context())
	if err != nil || doc.Issuer != "https://auth.example.com/acme" || doc.JwksURI != "https://auth.example.com/acme/.well-known/jwks.json" || len(doc.GrantTypesSupported) != 3 {
		t.Fatalf("discovery = %+v, error = %v", doc, err)
	}
	jwks, err := oidc.GetJwks(t.Context())
	if err != nil || len(jwks.Keys) != 1 || jwks.Keys[0].Kid != "key-1" || jwks.Keys[0].Crv != "P-256" {
		t.Fatalf("jwks = %+v, error = %v", jwks, err)
	}
	code, err := oidc.ExchangeAuthorizationCode(t.Context(), "cli_1", "csek", "code_1", "https://app.example/cb", "verifier_123")
	if err != nil || code.AccessToken != "at_1" || code.ExpiresIn != 3600 || code.RefreshToken == nil || *code.RefreshToken != "rt_1" || code.IDToken == nil || *code.IDToken != "id_1" {
		t.Fatalf("code exchange = %+v, error = %v", code, err)
	}
	refreshed, err := oidc.RefreshToken(t.Context(), "cli_1", "csek", "rt_1")
	if err != nil || refreshed.AccessToken != "at_1" {
		t.Fatalf("refresh = %+v, error = %v", refreshed, err)
	}
	credentials, err := oidc.RequestClientCredentialsToken(t.Context(), "cli_1", "csek", "openid profile")
	if err != nil || credentials.RefreshToken != nil {
		t.Fatalf("client credentials = %+v, error = %v", credentials, err)
	}
	if err := oidc.RevokeToken(t.Context(), "cli_1", "csek", "at_1"); err != nil {
		t.Fatalf("revoke error = %v", err)
	}
	introspection, err := oidc.IntrospectToken(t.Context(), "cli_1", "csek", "at_1")
	if err != nil || !introspection.Active || introspection.Sub == nil || *introspection.Sub != "user-1" || introspection.Exp == nil {
		t.Fatalf("introspect = %+v, error = %v", introspection, err)
	}
	userInfo, err := oidc.GetUserInfo(t.Context(), "at_live")
	if err != nil || userInfo.Sub != "user-1" || userInfo.Email == nil || userInfo.EmailVerified == nil || !*userInfo.EmailVerified {
		t.Fatalf("userinfo = %+v, error = %v", userInfo, err)
	}
}

func TestTSAuthFamiliesRoutes(t *testing.T) {
	type fakeResponse struct {
		status int
		body   string
	}
	responses := map[string]fakeResponse{
		"GET /v1/acme/users/user-1/mfa-factors":                            {http.StatusOK, `{"data":[{"id":"fac_1","type":"totp","confirmed":true,"createdAt":1700000000}]}`},
		"DELETE /v1/acme/users/user-1/mfa-factors/fac_1":                   {http.StatusNoContent, ""},
		"POST /v1/acme/users/user-1/mfa-factors/fac_1/regenerate-recovery": {http.StatusOK, `{"data":["code-1","code-2","code-3","code-4","code-5","code-6","code-7","code-8","code-9","code-10"]}`},
		"GET /v1/acme/users/user-1/passkeys":                               {http.StatusOK, `{"passkeys":[{"id":"pas_1","nickname":"yubi","algorithm":"ES256","createdAt":1700000000,"lastUsedAt":1700000100}]}`},
		"DELETE /v1/acme/users/user-1/passkeys/pas_1":                      {http.StatusNoContent, ""},
		"GET /v1/acme/users/user-1/organizations":                          {http.StatusOK, `[{"id":"mbr_1","role":"admin","created_at":1700000000,"organization":{"id":"org_1","name":"Acme","slug":"acme-org","created_at":1700000000}}]`},
		"POST /v1/acme/sessions/sess-1/revoke":                             {http.StatusNoContent, ""},
		"POST /v1/acme/organizations":                                      {http.StatusOK, `{"id":"org_1","name":"Acme","slug":"acme-org","created_at":1}`},
		"GET /v1/acme/organizations":                                       {http.StatusOK, `[{"id":"org_1","name":"Acme","slug":"acme-org","created_at":1}]`},
		"GET /v1/acme/organizations/org_1":                                 {http.StatusOK, `{"id":"org_1","name":"Acme","slug":"acme-org","created_at":1}`},
		"PATCH /v1/acme/organizations/org_1":                               {http.StatusOK, `{"id":"org_1","name":"Acme Inc","slug":"acme-org","created_at":1}`},
		"POST /v1/acme/organizations/org_1/memberships":                    {http.StatusOK, `{"id":"mbr_1","organization_id":"org_1","user_id":"user-1","role":"admin","created_at":1}`},
		"GET /v1/acme/organizations/org_1/memberships":                     {http.StatusOK, `[{"id":"mbr_1","organization_id":"org_1","user_id":"user-1","role":"admin","created_at":1,"user":{"id":"user-1","email":"ada@example.com","name":"Ada"}}]`},
		"PATCH /v1/acme/organizations/org_1/memberships/mbr_1":             {http.StatusOK, `{"id":"mbr_1","organization_id":"org_1","user_id":"user-1","role":"member","created_at":1}`},
		"DELETE /v1/acme/organizations/org_1/memberships/mbr_1":            {http.StatusNoContent, ""},
		"POST /v1/acme/organizations/org_1/invitations":                    {http.StatusOK, `{"id":"inv_1","organization_id":"org_1","email":"dev@example.com","role":"dev","status":"pending","expires_at":1700008600,"created_at":1}`},
		"GET /v1/acme/organizations/org_1/invitations":                     {http.StatusOK, `[{"id":"inv_1","organization_id":"org_1","email":"dev@example.com","role":"dev","status":"pending","expires_at":1700008600,"created_at":1}]`},
		"POST /v1/acme/organizations/org_1/invitations/inv_1/revoke":       {http.StatusOK, `{"id":"inv_1","organization_id":"org_1","email":"dev@example.com","role":"dev","status":"revoked","expires_at":1700008600,"created_at":1}`},
		"POST /v1/acme/organizations/org_1/domains":                        {http.StatusOK, `{"id":"dom_1","organization_id":"org_1","domain":"example.com","created_at":1}`},
		"GET /v1/acme/organizations/org_1/domains":                         {http.StatusOK, `[{"id":"dom_1","organization_id":"org_1","domain":"example.com","created_at":1}]`},
		"DELETE /v1/acme/organizations/org_1/domains/dom_1":                {http.StatusNoContent, ""},
		"POST /v1/acme/domains/example.com/enroll":                         {http.StatusOK, `{"org_id":"org_1"}`},
		"DELETE /v1/acme/organizations/org_1":                              {http.StatusNoContent, ""},
		"POST /v1/acme/connections":                                        {http.StatusCreated, `{"id":"con_1","type":"oidc","name":"Google","enabled":true,"org_id":"org_1","issuer_url":"https://accounts.google.com","client_id":"cli","created_at":1}`},
		"GET /v1/acme/connections":                                         {http.StatusOK, `{"data":[{"id":"con_1","type":"oidc","name":"Google","enabled":true,"org_id":"org_1","issuer_url":"https://accounts.google.com","client_id":"cli","created_at":1}]}`},
		"PATCH /v1/acme/connections/con_1":                                 {http.StatusOK, `{"id":"con_1","type":"oidc","name":"Google","enabled":false,"org_id":"org_1","issuer_url":"https://accounts.google.com","client_id":"cli","created_at":1}`},
		"DELETE /v1/acme/connections/con_1":                                {http.StatusNoContent, ""},
		"POST /v1/acme/directories":                                        {http.StatusCreated, `{"id":"dir_1","org_id":"org_1","token":"scim_once"}`},
		"GET /v1/acme/directories":                                         {http.StatusOK, `{"data":[{"id":"dir_1","org_id":"org_1","created_at":1}]}`},
		"POST /v1/acme/directories/dir_1/rotate":                           {http.StatusOK, `{"id":"dir_1","org_id":"org_1","token":"scim_rotated"}`},
		"DELETE /v1/acme/directories/dir_1":                                {http.StatusNoContent, ""},
		"POST /v1/acme/jwt-templates":                                      {http.StatusOK, `{"id":"tpl_1","name":"session","claims":{"aud":"app"},"lifetime_seconds":300,"created_at":1}`},
		"GET /v1/acme/jwt-templates":                                       {http.StatusOK, `[{"id":"tpl_1","name":"session","claims":{"aud":"app"},"lifetime_seconds":300,"created_at":1}]`},
		"PATCH /v1/acme/jwt-templates/session":                             {http.StatusOK, `{"id":"tpl_1","name":"session","claims":{"aud":"app"},"lifetime_seconds":600,"created_at":1}`},
		"POST /v1/acme/jwt-templates/session/tokens":                       {http.StatusOK, `{"jwt":"eyJhbGciOiJFUzI1NiJ9.payload.sig"}`},
		"DELETE /v1/acme/jwt-templates/session":                            {http.StatusNoContent, ""},
		"POST /v1/acme/webhooks/wh_1/test":                                 {http.StatusOK, `{"queued":true}`},
		"GET /v1/acme/webhooks/dead-letter":                                {http.StatusOK, `[{"id":"ep1:ev1","endpoint_id":"ep1","event_id":"ev1","attempts":8,"last_error":"connection refused","updated_at":1700000000}]`},
		"POST /v1/acme/webhooks/dead-letter/ep1:ev1/replay":                {http.StatusOK, `{"replayed":true}`},
		"GET /v1/acme/audit-events?limit=100":                              {http.StatusOK, `[{"id":"evt_1","instance_id":"inst_1","type":"user.created","data":{"user_id":"user-1"},"created_at":1700000000,"integrity_version":1,"integrity_hash":"h","integrity_signature":"s","integrity_key_fingerprint":"f"}]`},
		"GET /v1/acme/billing":                                             {http.StatusOK, `{"plan":"pro","status":"active","stripe_customer_id":"cus_1","stripe_subscription_id":"sub_1","current_period_end":1700000000,"user_count":5,"quota":10}`},
		"POST /v1/acme/billing/checkout":                                   {http.StatusOK, `{"checkout_url":"https://checkout.example.com/session"}`},
		"POST /v1/acme/billing/portal":                                     {http.StatusOK, `{"portal_url":"https://billing.example.com/portal"}`},
		"POST /v1/acme/billing/enforce-check":                              {http.StatusOK, `{"allowed":true,"plan":"pro","quota":10}`},
		"POST /v1/acme/billing/quota/reconcile":                            {http.StatusOK, `{"claims_examined":5,"claims_removed":1,"claims_retained":4}`},
		"GET /v1/acme/instance":                                            {http.StatusOK, `{"id":"inst_1","slug":"acme","name":"Acme Auth","logo_url":null,"theme_color":"#223344","created_at":1}`},
		"PATCH /v1/acme/instance":                                          {http.StatusOK, `{"id":"inst_1","slug":"acme","name":"Acme Auth Inc","logo_url":null,"theme_color":"#223344"}`},
	}
	var requests []string
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if got := r.Header.Get("Authorization"); got != "Bearer sk_test_key" {
			t.Errorf("authorization = %q", got)
		}
		key := r.Method + " " + r.URL.RequestURI()
		requests = append(requests, key)
		response, ok := responses[key]
		if !ok {
			t.Errorf("unexpected request %q", key)
			http.NotFound(w, r)
			return
		}
		if response.body != "" {
			w.Header().Set("Content-Type", "application/json")
		}
		w.WriteHeader(response.status)
		if response.body != "" {
			_, _ = w.Write([]byte(response.body))
		}
	}))
	defer server.Close()

	instance := newTSAuthTestInstance(t, Config{APIBaseURL: server.URL, AccessToken: "sk_test_key"})
	ctx := t.Context()

	factors, err := instance.Users.ListMFAFactors(ctx, "user-1")
	if err != nil || len(factors) != 1 || factors[0].CreatedAt != 1700000000 || !factors[0].Confirmed {
		t.Fatalf("mfa factors = %+v, error = %v", factors, err)
	}
	if err := instance.Users.DeleteMFAFactor(ctx, "user-1", "fac_1"); err != nil {
		t.Fatal(err)
	}
	codes, err := instance.Users.RegenerateRecoveryCodes(ctx, "user-1", "fac_1")
	if err != nil || len(codes) != 10 || codes[0] != "code-1" {
		t.Fatalf("recovery codes = %+v, error = %v", codes, err)
	}
	passkeys, err := instance.Users.ListPasskeys(ctx, "user-1")
	if err != nil || len(passkeys) != 1 || passkeys[0].Nickname != "yubi" || passkeys[0].LastUsedAt == nil {
		t.Fatalf("passkeys = %+v, error = %v", passkeys, err)
	}
	if err := instance.Users.DeletePasskey(ctx, "user-1", "pas_1"); err != nil {
		t.Fatal(err)
	}
	userOrgs, err := instance.Users.ListOrganizations(ctx, "user-1")
	if err != nil || len(userOrgs) != 1 || userOrgs[0].Organization == nil || userOrgs[0].Organization.Slug != "acme-org" {
		t.Fatalf("user organizations = %+v, error = %v", userOrgs, err)
	}
	if err := instance.Sessions.Revoke(ctx, "sess-1"); err != nil {
		t.Fatal(err)
	}

	org, err := instance.Organizations.Create(ctx, CreateTSAuthOrganizationRequest{Name: "Acme", Slug: "acme-org"})
	if err != nil || org.ID != "org_1" {
		t.Fatalf("organization create = %+v, error = %v", org, err)
	}
	if orgs, err := instance.Organizations.List(ctx); err != nil || len(orgs) != 1 {
		t.Fatalf("organization list error = %v", err)
	}
	if _, err := instance.Organizations.Get(ctx, "org_1"); err != nil {
		t.Fatal(err)
	}
	if _, err := instance.Organizations.Rename(ctx, "org_1", "Acme Inc"); err != nil {
		t.Fatal(err)
	}
	if _, err := instance.Organizations.AddMember(ctx, "org_1", "user-1", "admin"); err != nil {
		t.Fatal(err)
	}
	memberships, err := instance.Organizations.ListMemberships(ctx, "org_1")
	if err != nil || len(memberships) != 1 || memberships[0].User == nil || memberships[0].User.Email != "ada@example.com" {
		t.Fatalf("memberships = %+v, error = %v", memberships, err)
	}
	if _, err := instance.Organizations.UpdateMemberRole(ctx, "org_1", "mbr_1", "member"); err != nil {
		t.Fatal(err)
	}
	if err := instance.Organizations.RemoveMember(ctx, "org_1", "mbr_1"); err != nil {
		t.Fatal(err)
	}
	if _, err := instance.Organizations.CreateInvitation(ctx, "org_1", "dev@example.com", "dev"); err != nil {
		t.Fatal(err)
	}
	if _, err := instance.Organizations.ListInvitations(ctx, "org_1"); err != nil {
		t.Fatal(err)
	}
	revoked, err := instance.Organizations.RevokeInvitation(ctx, "org_1", "inv_1")
	if err != nil || revoked.Status != "revoked" {
		t.Fatalf("revoke invitation = %+v, error = %v", revoked, err)
	}
	if _, err := instance.Organizations.AddDomain(ctx, "org_1", "example.com"); err != nil {
		t.Fatal(err)
	}
	if _, err := instance.Organizations.ListDomains(ctx, "org_1"); err != nil {
		t.Fatal(err)
	}
	if err := instance.Organizations.RemoveDomain(ctx, "org_1", "dom_1"); err != nil {
		t.Fatal(err)
	}
	enrollment, err := instance.Organizations.EnrollDomainUser(ctx, "example.com", "user-1")
	if err != nil || enrollment.OrgID != "org_1" {
		t.Fatalf("domain enrollment = %+v, error = %v", enrollment, err)
	}
	if err := instance.Organizations.Delete(ctx, "org_1"); err != nil {
		t.Fatal(err)
	}

	connection, err := instance.Connections.CreateOIDC(ctx, CreateTSAuthOIDCConnectionRequest{
		Name: "Google", OrgID: "org_1", IssuerURL: "https://accounts.google.com", ClientID: "cli", ClientSecret: "secret",
	})
	if err != nil || connection.ID != "con_1" || connection.Type != "oidc" || connection.OrgID == nil || *connection.OrgID != "org_1" {
		t.Fatalf("connection create = %+v, error = %v", connection, err)
	}
	if connections, err := instance.Connections.List(ctx); err != nil || len(connections) != 1 {
		t.Fatalf("connection list error = %v", err)
	}
	disabled := false
	if _, err := instance.Connections.Update(ctx, "con_1", UpdateTSAuthConnectionRequest{Enabled: &disabled}); err != nil {
		t.Fatal(err)
	}
	if err := instance.Connections.Delete(ctx, "con_1"); err != nil {
		t.Fatal(err)
	}

	directory, err := instance.Directories.Create(ctx, "org_1")
	if err != nil || directory.Token != "scim_once" {
		t.Fatalf("directory create = %+v, error = %v", directory, err)
	}
	if directories, err := instance.Directories.List(ctx); err != nil || len(directories) != 1 || directories[0].Token != "" {
		t.Fatalf("directory list = %+v, error = %v", directories, err)
	}
	rotated, err := instance.Directories.RotateToken(ctx, "dir_1")
	if err != nil || rotated.Token != "scim_rotated" {
		t.Fatalf("directory rotate = %+v, error = %v", rotated, err)
	}
	if err := instance.Directories.Delete(ctx, "dir_1"); err != nil {
		t.Fatal(err)
	}

	template, err := instance.JWTTemplates.Create(ctx, CreateTSAuthJWTTemplateRequest{Name: "session", Claims: map[string]string{"aud": "app"}})
	if err != nil || template.LifetimeSeconds != 300 {
		t.Fatalf("template create = %+v, error = %v", template, err)
	}
	if _, err := instance.JWTTemplates.List(ctx); err != nil {
		t.Fatal(err)
	}
	lifetime := uint32(600)
	if _, err := instance.JWTTemplates.Update(ctx, "session", UpdateTSAuthJWTTemplateRequest{LifetimeSeconds: &lifetime}); err != nil {
		t.Fatal(err)
	}
	minted, err := instance.JWTTemplates.MintToken(ctx, "session", "user-1")
	if err != nil || minted.JWT != "eyJhbGciOiJFUzI1NiJ9.payload.sig" {
		t.Fatalf("minted token = %+v, error = %v", minted, err)
	}
	if err := instance.JWTTemplates.Delete(ctx, "session"); err != nil {
		t.Fatal(err)
	}

	test, err := instance.Webhooks.SendTestEvent(ctx, "wh_1")
	if err != nil || !test.Queued {
		t.Fatalf("webhook test = %+v, error = %v", test, err)
	}
	dead, err := instance.Webhooks.ListDeadLetter(ctx)
	if err != nil || len(dead) != 1 || dead[0].ID != "ep1:ev1" || dead[0].LastError == nil {
		t.Fatalf("dead letter = %+v, error = %v", dead, err)
	}
	replay, err := instance.Webhooks.ReplayDeadDelivery(ctx, "ep1:ev1")
	if err != nil || !replay.Replayed {
		t.Fatalf("replay = %+v, error = %v", replay, err)
	}

	events, err := instance.AuditEvents.List(ctx, 100)
	if err != nil || len(events) != 1 || events[0].IntegrityVersion != 1 || events[0].ActorID != "" {
		t.Fatalf("audit events = %+v, error = %v", events, err)
	}

	overview, err := instance.Billing.GetOverview(ctx)
	if err != nil || overview.Plan != "pro" || overview.UserCount != 5 || overview.StripeCustomerID == nil {
		t.Fatalf("billing overview = %+v, error = %v", overview, err)
	}
	checkout, err := instance.Billing.CreateCheckout(ctx, "pro")
	if err != nil || checkout.CheckoutURL != "https://checkout.example.com/session" {
		t.Fatalf("checkout = %+v, error = %v", checkout, err)
	}
	portal, err := instance.Billing.CreatePortalSession(ctx)
	if err != nil || portal.PortalURL != "https://billing.example.com/portal" {
		t.Fatalf("portal = %+v, error = %v", portal, err)
	}
	decision, err := instance.Billing.EnforceQuota(ctx, 5)
	if err != nil || !decision.Allowed || decision.Quota != 10 {
		t.Fatalf("quota decision = %+v, error = %v", decision, err)
	}
	reconciliation, err := instance.Billing.ReconcileQuota(ctx)
	if err != nil || reconciliation.ClaimsExamined != 5 || reconciliation.ClaimsRemoved != 1 {
		t.Fatalf("quota reconciliation = %+v, error = %v", reconciliation, err)
	}

	record, err := instance.Instance.Get(ctx)
	if err != nil || record.Slug != "acme" || record.CreatedAt == nil || *record.CreatedAt != 1 {
		t.Fatalf("instance get = %+v, error = %v", record, err)
	}
	newName := "Acme Auth Inc"
	updated, err := instance.Instance.Update(ctx, TSAuthInstanceSettings{Name: &newName})
	if err != nil || updated.Name != "Acme Auth Inc" || updated.CreatedAt != nil {
		t.Fatalf("instance update = %+v, error = %v", updated, err)
	}

	want := make([]string, 0, len(responses))
	for _, key := range []string{
		"GET /v1/acme/users/user-1/mfa-factors",
		"DELETE /v1/acme/users/user-1/mfa-factors/fac_1",
		"POST /v1/acme/users/user-1/mfa-factors/fac_1/regenerate-recovery",
		"GET /v1/acme/users/user-1/passkeys",
		"DELETE /v1/acme/users/user-1/passkeys/pas_1",
		"GET /v1/acme/users/user-1/organizations",
		"POST /v1/acme/sessions/sess-1/revoke",
		"POST /v1/acme/organizations",
		"GET /v1/acme/organizations",
		"GET /v1/acme/organizations/org_1",
		"PATCH /v1/acme/organizations/org_1",
		"POST /v1/acme/organizations/org_1/memberships",
		"GET /v1/acme/organizations/org_1/memberships",
		"PATCH /v1/acme/organizations/org_1/memberships/mbr_1",
		"DELETE /v1/acme/organizations/org_1/memberships/mbr_1",
		"POST /v1/acme/organizations/org_1/invitations",
		"GET /v1/acme/organizations/org_1/invitations",
		"POST /v1/acme/organizations/org_1/invitations/inv_1/revoke",
		"POST /v1/acme/organizations/org_1/domains",
		"GET /v1/acme/organizations/org_1/domains",
		"DELETE /v1/acme/organizations/org_1/domains/dom_1",
		"POST /v1/acme/domains/example.com/enroll",
		"DELETE /v1/acme/organizations/org_1",
		"POST /v1/acme/connections",
		"GET /v1/acme/connections",
		"PATCH /v1/acme/connections/con_1",
		"DELETE /v1/acme/connections/con_1",
		"POST /v1/acme/directories",
		"GET /v1/acme/directories",
		"POST /v1/acme/directories/dir_1/rotate",
		"DELETE /v1/acme/directories/dir_1",
		"POST /v1/acme/jwt-templates",
		"GET /v1/acme/jwt-templates",
		"PATCH /v1/acme/jwt-templates/session",
		"POST /v1/acme/jwt-templates/session/tokens",
		"DELETE /v1/acme/jwt-templates/session",
		"POST /v1/acme/webhooks/wh_1/test",
		"GET /v1/acme/webhooks/dead-letter",
		"POST /v1/acme/webhooks/dead-letter/ep1:ev1/replay",
		"GET /v1/acme/audit-events?limit=100",
		"GET /v1/acme/billing",
		"POST /v1/acme/billing/checkout",
		"POST /v1/acme/billing/portal",
		"POST /v1/acme/billing/enforce-check",
		"POST /v1/acme/billing/quota/reconcile",
		"GET /v1/acme/instance",
		"PATCH /v1/acme/instance",
	} {
		if _, ok := responses[key]; !ok {
			t.Fatalf("expected request %q has no canned response", key)
		}
		want = append(want, key)
	}
	if !reflect.DeepEqual(requests, want) {
		t.Fatalf("requests:\n got %#v\nwant %#v", requests, want)
	}
}
