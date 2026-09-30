package sdk

import (
	"encoding/json"
	"errors"
	"net/http"
	"net/http/httptest"
	"net/url"
	"strings"
	"testing"
	"time"
)

// The cryptographic provider here is the accepted OIDC test fixture. This
// verifies the wrapper boundary, not hosted identity or current durable grants.
func TestTSAuthResourceApplicationRetainsBrowserBindingAndOriginIDAudience(t *testing.T) {
	f := newOIDCFixture(t)
	const audience = "https://api.example.test/management%2Fscope?x=a%2Fb&x=+"
	instance := newTSAuthTestInstance(t, Config{TSAuthBaseURL: f.server.URL})
	app, err := instance.NewResourceApplication(TSAuthResourceApplicationConfig{OIDC: TSAuthOIDCApplicationConfig{Issuer: f.issuer, Authentication: TSAuthOAuthClientAuthentication{ClientID: "client", ClientSecret: "secret", Method: TSAuthClientSecretBasic}, RedirectURI: "https://app.example.test/callback", RequireSession: true}, ResourceAudience: audience})
	if err != nil {
		t.Fatal(err)
	}
	attempt, err := app.BeginAuthorization(t.Context(), []string{"openid", "read"})
	if err != nil {
		t.Fatal(err)
	}
	u, _ := url.Parse(attempt.Authorization.AuthorizationURL)
	if len(u.Query()["resource"]) != 1 || u.Query().Get("resource") != audience || u.Query().Get("state") != attempt.Authorization.State || u.Query().Get("nonce") != attempt.Authorization.Nonce || u.Query().Get("code_challenge_method") != "S256" {
		t.Fatal("browser/resource binding changed")
	}
	f.token = f.sign(t, f.claims(attempt.Authorization.Nonce), nil)
	f.formHandler = func(w http.ResponseWriter, r *http.Request) {
		r.ParseForm()
		if r.PostForm.Get("resource") != audience || r.PostForm.Get("code_verifier") != attempt.Authorization.CodeVerifier {
			t.Error("code exchange lost resource or PKCE")
		}
		json.NewEncoder(w).Encode(map[string]any{"access_token": "access", "token_type": "Bearer", "expires_in": 60, "scope": "openid read", "id_token": f.token, "refresh_token": "refresh"})
	}
	before := f.requests
	if _, err := app.ExchangeCode(t.Context(), TSAuthOIDCCallback{Code: "code", State: "wrong"}, *attempt); err == nil || f.requests != before {
		t.Fatal("wrong state dispatched")
	}
	changed := *attempt
	changed.ResourceAudience = "https://other.example.test/a"
	if _, err := app.ExchangeCode(t.Context(), TSAuthOIDCCallback{Code: "code", State: attempt.Authorization.State}, changed); err == nil || f.requests != before {
		t.Fatal("changed resource dispatched")
	}
	login, err := app.ExchangeCode(t.Context(), TSAuthOIDCCallback{Code: "code", State: attempt.Authorization.State}, *attempt)
	if err != nil || login.Grant.Audience() != audience || login.Identity.Audience[0] != "client" || login.Identity.SessionID == nil {
		t.Fatalf("verified origin identity/resource binding: %v", err)
	}
	claims := f.claims(attempt.Authorization.Nonce)
	claims["aud"] = audience
	f.token = f.sign(t, claims, nil)
	before = f.requests
	_, err = app.ExchangeCode(t.Context(), TSAuthOIDCCallback{Code: "other-code", State: attempt.Authorization.State}, *attempt)
	var failure *TSAuthOIDCError
	if !errors.As(err, &failure) || !failure.Uncertain || f.requests != before+1 {
		t.Fatalf("resource audience became ID identity or exchange replayed: %v", err)
	}
}

func TestTSAuthResourceDeviceExplicitPollingAndBinding(t *testing.T) {
	const audience = "https://api.example.test/resource"
	var issuer, verificationURI string
	calls := 0
	polls := 0
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		calls++
		r.ParseForm()
		w.Header().Set("Content-Type", "application/json")
		if r.URL.Path == "/acme/oauth/device_authorization" {
			if r.PostForm.Get("resource") != audience || r.PostForm.Get("scope") != "read" {
				t.Error("device selection changed")
			}
			json.NewEncoder(w).Encode(map[string]any{"device_code": strings.Repeat("d", 75), "user_code": "ABCD-EFGH", "verification_uri": verificationURI, "verification_uri_complete": verificationURI + "?user_code=ABCD-EFGH", "expires_in": 600, "interval": 5})
			return
		}
		polls++
		if r.PostForm.Get("grant_type") != TSAuthDeviceCodeGrant || r.PostForm.Get("device_code") != strings.Repeat("d", 75) {
			t.Error("device grant changed")
		}
		if polls == 1 {
			w.WriteHeader(400)
			w.Write([]byte(`{"error":"slow_down"}`))
			return
		}
		json.NewEncoder(w).Encode(map[string]any{"access_token": "access", "token_type": "Bearer", "expires_in": 60, "scope": "read", "refresh_token": nil})
	}))
	defer server.Close()
	issuer = server.URL + "/acme"
	verificationURI = server.URL + "/ui/acme/device"
	instance := newTSAuthTestInstance(t, Config{TSAuthBaseURL: server.URL})
	client, err := instance.NewResourceClient(TSAuthResourceClientConfig{Issuer: issuer, ResourceAudience: audience, Authentication: TSAuthOAuthClientAuthentication{ClientID: "public", Method: TSAuthClientAuthenticationNone}})
	if err != nil {
		t.Fatal(err)
	}
	device, err := client.BeginDeviceAuthorization(t.Context(), []string{"read"})
	if err != nil || device.VerificationURI != verificationURI || device.VerificationURIComplete != verificationURI+"?user_code=ABCD-EFGH" {
		t.Fatalf("canonical public device route: %v", err)
	}
	before := calls
	if _, err := client.PollDevice(t.Context(), device, false); err == nil || calls != before {
		t.Fatal("early polling dispatched")
	}
	// Advance only private synthetic test state; production uses monotonic time.
	device.nextPoll = time.Now().Add(-time.Second)
	_, err = client.PollDevice(t.Context(), device, true)
	var failure *TSAuthOIDCError
	if !errors.As(err, &failure) || failure.Code != "slow_down" || device.Interval != 10 || polls != 1 {
		t.Fatalf("slow down semantics: %v", err)
	}
	device.Interval = 0
	if _, err := client.PollDevice(t.Context(), device, false); err == nil || polls != 1 {
		t.Fatal("public view bypassed private interval")
	}
	device.nextPoll = time.Now().Add(-time.Second)
	grant, err := client.PollDevice(t.Context(), device, false)
	if err != nil || grant.Audience() != audience || polls != 2 {
		t.Fatalf("device completion: %v", err)
	}
	if _, err := client.PollDevice(t.Context(), device, false); err == nil || polls != 2 {
		t.Fatal("completed device redispatched")
	}
}

func TestTSAuthResourceDeviceRejectsInvalidURI(t *testing.T) {
	for _, test := range []struct{ name, path, suffix string }{
		{"duplicate-code", "/ui/acme/device", "?user_code=ABCD-EFGH&user_code=OTHER"},
		{"invalid-percent", "/ui/acme/device", "?user_code=ABCD-EFGH&invalid=%ZZ"},
		{"empty-fragment", "/ui/acme/device", "?user_code=ABCD-EFGH#"},
		{"extra-query", "/ui/acme/device", "?user_code=ABCD-EFGH&extra=1"},
		{"issuer-route", "/acme/oauth/device", "?user_code=ABCD-EFGH"},
		{"other-instance", "/ui/other/device", "?user_code=ABCD-EFGH"},
	} {
		t.Run(test.name, func(t *testing.T) {
			var verificationURI string
			calls := 0
			server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				calls++
				w.Header().Set("Content-Type", "application/json")
				json.NewEncoder(w).Encode(map[string]any{"device_code": strings.Repeat("d", 75), "user_code": "ABCD-EFGH", "verification_uri": verificationURI, "verification_uri_complete": verificationURI + test.suffix, "expires_in": 600, "interval": 5})
			}))
			defer server.Close()
			verificationURI = server.URL + test.path
			instance := newTSAuthTestInstance(t, Config{TSAuthBaseURL: server.URL})
			client, err := instance.NewResourceClient(TSAuthResourceClientConfig{Issuer: server.URL + "/acme", ResourceAudience: "https://api.example.test/resource", Authentication: TSAuthOAuthClientAuthentication{ClientID: "origin", Method: TSAuthClientAuthenticationNone}})
			if err != nil {
				t.Fatal(err)
			}
			_, err = client.BeginDeviceAuthorization(t.Context(), []string{"read"})
			var failure *TSAuthOIDCError
			if !errors.As(err, &failure) || failure.Code != "invalid_device_response" || !failure.Uncertain || calls != 1 {
				t.Fatalf("ambiguous device URI admitted or replayed: %v", err)
			}
		})
	}
}
