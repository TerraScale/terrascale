package sdk

import (
	"context"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/sha256"
	"encoding/base64"
	"encoding/json"
	"errors"
	"io"
	"maps"
	"net/http"
	"net/http/httptest"
	"net/url"
	"reflect"
	"slices"
	"strconv"
	"strings"
	"testing"
	"time"
)

func TestTSAuthOIDCAuthenticationMethods(t *testing.T) {
	for _, method := range []TSAuthOAuthClientAuthenticationMethod{TSAuthClientSecretBasic, TSAuthClientSecretPost, TSAuthClientAuthenticationNone} {
		t.Run(string(method), func(t *testing.T) {
			auth := TSAuthOAuthClientAuthentication{ClientID: "client: +%", ClientSecret: "secret: +%&=", Method: method}
			if method == TSAuthClientAuthenticationNone {
				auth.ClientSecret = ""
			}
			calls := 0
			server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				calls++
				if err := r.ParseForm(); err != nil {
					t.Error(err)
				}
				switch method {
				case TSAuthClientSecretBasic:
					id, secret, ok := r.BasicAuth()
					if !ok {
						t.Error("missing Basic auth")
					}
					id, _ = url.QueryUnescape(id)
					secret, _ = url.QueryUnescape(secret)
					if id != auth.ClientID || secret != auth.ClientSecret || r.PostForm.Has("client_secret") || r.PostForm.Has("client_id") {
						t.Error("Basic credentials or form diverged")
					}
				case TSAuthClientSecretPost:
					if r.Header.Get("Authorization") != "" || r.PostForm.Get("client_id") != auth.ClientID || r.PostForm.Get("client_secret") != auth.ClientSecret {
						t.Error("post method diverged")
					}
				case TSAuthClientAuthenticationNone:
					if r.Header.Get("Authorization") != "" || r.PostForm.Get("client_id") != auth.ClientID || r.PostForm.Has("client_secret") {
						t.Error("public method diverged")
					}
				}
				w.Header().Set("Content-Type", "application/json")
				io.WriteString(w, `{"access_token":"access","token_type":"Bearer","expires_in":60}`)
			}))
			defer server.Close()
			oidc := newTSAuthTestInstance(t, Config{TSAuthBaseURL: server.URL}).OIDC
			if _, err := oidc.RequestClientCredentialsToken(t.Context(), auth, "scope"); err != nil {
				t.Fatal(err)
			}
			if calls != 1 {
				t.Fatalf("requests=%d", calls)
			}
		})
	}
}

func TestTSAuthOIDCAuthenticationRejectsUnboundMethod(t *testing.T) {
	for _, auth := range []TSAuthOAuthClientAuthentication{{ClientID: "client", ClientSecret: "secret"}, {ClientID: "client", Method: TSAuthClientSecretBasic}, {ClientID: "client", ClientSecret: "secret", Method: TSAuthClientAuthenticationNone}} {
		if err := auth.validate(); err == nil {
			t.Fatal("invalid authentication admitted")
		}
	}
}

type oidcFixture struct {
	server      *httptest.Server
	key         *ecdsa.PrivateKey
	issuer      string
	document    TSAuthDiscoveryDocument
	jwks        TSAuthJWKS
	token       string
	requests    int
	formHandler func(http.ResponseWriter, *http.Request)
}

func newOIDCFixture(t *testing.T) *oidcFixture {
	t.Helper()
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	f := &oidcFixture{key: key}
	f.server = httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		switch r.URL.Path {
		case "/acme/.well-known/openid-configuration":
			if r.Header.Get("Authorization") != "" {
				t.Error("discovery leaked credentials")
			}
			json.NewEncoder(w).Encode(f.document)
		case "/acme/.well-known/jwks.json":
			if r.Header.Get("Authorization") != "" {
				t.Error("JWKS leaked credentials")
			}
			json.NewEncoder(w).Encode(f.jwks)
		case "/acme/oauth/token", "/acme/oauth/revoke":
			f.requests++
			if f.formHandler != nil {
				f.formHandler(w, r)
				return
			}
			json.NewEncoder(w).Encode(TSAuthTokenResponse{AccessToken: "access", TokenType: "Bearer", ExpiresIn: 60, IDToken: &f.token})
		default:
			http.NotFound(w, r)
		}
	}))
	t.Cleanup(f.server.Close)
	f.issuer = f.server.URL + "/acme"
	f.document = TSAuthDiscoveryDocument{Issuer: f.issuer, AuthorizationEndpoint: f.issuer + "/oauth/authorize", TokenEndpoint: f.issuer + "/oauth/token", UserinfoEndpoint: f.issuer + "/oauth/userinfo", JwksURI: f.issuer + "/.well-known/jwks.json", RevocationEndpoint: f.issuer + "/oauth/revoke", EndSessionEndpoint: f.issuer + "/oauth/logout", ResponseTypesSupported: []string{"code"}, CodeChallengeMethodsSupported: []string{"S256"}, IdTokenSigningAlgValuesSupported: []string{"ES256"}, TokenEndpointAuthMethodsSupported: []string{"client_secret_basic", "client_secret_post", "none"}}
	f.jwks = TSAuthJWKS{Keys: []TSAuthJWK{{Kid: "key1", Kty: "EC", Crv: "P-256", Use: "sig", Alg: "ES256", X: base64.RawURLEncoding.EncodeToString(key.X.FillBytes(make([]byte, 32))), Y: base64.RawURLEncoding.EncodeToString(key.Y.FillBytes(make([]byte, 32)))}}}
	return f
}
func (f *oidcFixture) application(t *testing.T) *TSAuthOIDCApplication {
	t.Helper()
	oidc := newTSAuthTestInstance(t, Config{TSAuthBaseURL: f.server.URL}).OIDC
	app, err := oidc.NewApplication(TSAuthOIDCApplicationConfig{Issuer: f.issuer, Authentication: TSAuthOAuthClientAuthentication{ClientID: "client", ClientSecret: "secret", Method: TSAuthClientSecretBasic}, RedirectURI: "https://app.example/callback?intent=login", PostLogoutRedirectURIs: []string{"https://app.example/logout"}, RequireSession: true})
	if err != nil {
		t.Fatal(err)
	}
	return app
}
func (f *oidcFixture) claims(nonce string) map[string]any {
	now := time.Now().Unix()
	hash := sha256.Sum256([]byte("access"))
	return map[string]any{"iss": f.issuer, "sub": "01ARZ3NDEKTSV4RRFFQ69G5FA9", "aud": "client", "exp": now + 60, "iat": now - 1, "nonce": nonce, "token_use": "id", "sid": "01ARZ3NDEKTSV4RRFFQ69G5FAB", "at_hash": base64.RawURLEncoding.EncodeToString(hash[:16])}
}
func (f *oidcFixture) sign(t *testing.T, claims map[string]any, header map[string]any) string {
	t.Helper()
	body, err := json.Marshal(claims)
	if err != nil {
		t.Fatal(err)
	}
	return f.signBytes(t, body, header)
}
func (f *oidcFixture) signBytes(t *testing.T, body []byte, header map[string]any) string {
	t.Helper()
	if header == nil {
		header = map[string]any{"alg": "ES256", "typ": "JWT", "kid": "key1"}
	}
	head, err := json.Marshal(header)
	if err != nil {
		t.Fatal(err)
	}
	unsigned := base64.RawURLEncoding.EncodeToString(head) + "." + base64.RawURLEncoding.EncodeToString(body)
	hash := sha256.Sum256([]byte(unsigned))
	r, s, err := ecdsa.Sign(rand.Reader, f.key, hash[:])
	if err != nil {
		t.Fatal(err)
	}
	signature := append(r.FillBytes(make([]byte, 32)), s.FillBytes(make([]byte, 32))...)
	return unsigned + "." + base64.RawURLEncoding.EncodeToString(signature)
}
func assertOIDCCode(t *testing.T, err error, code string) {
	t.Helper()
	var failure *TSAuthOIDCError
	if !errors.As(err, &failure) || failure.Code != code {
		t.Fatalf("error=%v, want %s", err, code)
	}
}

func TestTSAuthOIDCVerifiedAuthorization(t *testing.T) {
	f := newOIDCFixture(t)
	app := f.application(t)
	attempt, err := app.BeginAuthorization(t.Context(), []string{"openid", "email"})
	if err != nil {
		t.Fatal(err)
	}
	u, err := url.Parse(attempt.AuthorizationURL)
	if err != nil {
		t.Fatal(err)
	}
	challenge := sha256.Sum256([]byte(attempt.CodeVerifier))
	if u.Query().Get("code_challenge") != base64.RawURLEncoding.EncodeToString(challenge[:]) || u.Query().Get("nonce") != attempt.Nonce || u.Query().Get("state") != attempt.State {
		t.Fatal("authorization not PKCE/nonce/state bound")
	}
	f.token = f.sign(t, f.claims(attempt.Nonce), nil)
	login, err := app.ExchangeCode(t.Context(), TSAuthOIDCCallback{Code: "code", State: attempt.State}, *attempt)
	if err != nil {
		t.Fatal(err)
	}
	if login.Identity.Subject != "01ARZ3NDEKTSV4RRFFQ69G5FA9" || login.Identity.Issuer != f.issuer || login.Identity.SessionID == nil {
		t.Fatal("identity binding lost")
	}
	_, err = app.ExchangeCode(t.Context(), TSAuthOIDCCallback{Code: "code", State: "replayed-state"}, *attempt)
	assertOIDCCode(t, err, "invalid_authorization_attempt")
	if f.requests != 1 {
		t.Fatal("invalid callback reached token endpoint")
	}
	logout, err := app.EndSessionURL(t.Context(), "https://app.example/logout", "logout-state")
	if err != nil {
		t.Fatal(err)
	}
	u, _ = url.Parse(logout)
	if u.Query().Get("client_id") != "client" || u.Query().Has("id_token_hint") {
		t.Fatal("logout contract diverged")
	}
	_, err = app.EndSessionURL(t.Context(), "https://app.example/logout/other", "state")
	assertOIDCCode(t, err, "unregistered_logout_redirect")
}

func TestTSAuthOIDCVerificationDenials(t *testing.T) {
	cases := []struct {
		name, code string
		mutate     func(map[string]any)
	}{
		{"UUID subject", "invalid_identity_claims", func(c map[string]any) { c["sub"] = "0190a6e4-7c3b-7d2e-8f00-0123456789ab" }},
		{"prefixed subject", "invalid_identity_claims", func(c map[string]any) { c["sub"] = "usr_01ARZ3NDEKTSV4RRFFQ69G5FAV" }},
		{"lowercase subject", "invalid_identity_claims", func(c map[string]any) { c["sub"] = "01arz3ndektsv4rrffq69g5fav" }},
		{"invalid session ID", "invalid_session_binding", func(c map[string]any) { c["sid"] = "session" }},
		{"issuer", "invalid_identity_claims", func(c map[string]any) { c["iss"] = "https://evil.example/acme" }},
		{"audience", "invalid_identity_claims", func(c map[string]any) { c["aud"] = "another-client" }},
		{"access token", "invalid_identity_claims", func(c map[string]any) { c["token_use"] = "access" }},
		{"expired", "token_expired_or_not_yet_valid", func(c map[string]any) { c["exp"] = time.Now().Unix() - 1 }},
		{"future", "token_expired_or_not_yet_valid", func(c map[string]any) { c["iat"] = time.Now().Unix() + 3600; c["exp"] = time.Now().Unix() + 7200 }},
		{"nonce", "nonce_mismatch", func(c map[string]any) { c["nonce"] = "other" }},
		{"sessionless", "invalid_session_binding", func(c map[string]any) { delete(c, "sid") }},
		{"multi audience", "invalid_identity_claims", func(c map[string]any) { c["aud"] = []string{"client", "other"} }},
		{"access hash", "access_token_hash_mismatch", func(c map[string]any) { c["at_hash"] = "wrong" }},
		{"fractional expiry", "invalid_claims", func(c map[string]any) { c["exp"] = 1.25 }},
	}
	for _, test := range cases {
		t.Run(test.name, func(t *testing.T) {
			f := newOIDCFixture(t)
			app := f.application(t)
			claims := f.claims("nonce")
			test.mutate(claims)
			_, err := app.VerifyIDToken(t.Context(), f.sign(t, claims, nil), TSAuthIDTokenVerificationOptions{Nonce: "nonce", AccessToken: "access"})
			assertOIDCCode(t, err, test.code)
		})
	}
}

func TestTSAuthOIDCSigningKeyAndHeaderDenials(t *testing.T) {
	cases := []struct {
		name, code string
		mutate     func(*oidcFixture, map[string]any)
	}{
		{"algorithm confusion", "invalid_token_header", func(f *oidcFixture, h map[string]any) { h["alg"] = "HS256" }},
		{"missing kid", "invalid_token_header", func(f *oidcFixture, h map[string]any) { delete(h, "kid") }},
		{"untrusted key URI", "unsupported_token_header", func(f *oidcFixture, h map[string]any) { h["jku"] = "http://127.0.0.1/secrets" }},
		{"unknown kid", "invalid_signing_key", func(f *oidcFixture, h map[string]any) { h["kid"] = "unknown" }},
		{"duplicate key", "ambiguous_jwks", func(f *oidcFixture, h map[string]any) { f.jwks.Keys = append(f.jwks.Keys, f.jwks.Keys[0]) }},
		{"private key", "invalid_signing_key", func(f *oidcFixture, h map[string]any) { f.jwks.Keys[0].D = "private" }},
		{"invalid curve", "invalid_signing_key", func(f *oidcFixture, h map[string]any) {
			f.jwks.Keys[0].X = base64.RawURLEncoding.EncodeToString(make([]byte, 32))
			f.jwks.Keys[0].Y = f.jwks.Keys[0].X
		}},
	}
	for _, test := range cases {
		t.Run(test.name, func(t *testing.T) {
			f := newOIDCFixture(t)
			app := f.application(t)
			header := map[string]any{"alg": "ES256", "typ": "JWT", "kid": "key1"}
			test.mutate(f, header)
			_, err := app.VerifyIDToken(t.Context(), f.sign(t, f.claims("nonce"), header), TSAuthIDTokenVerificationOptions{Nonce: "nonce"})
			assertOIDCCode(t, err, test.code)
		})
	}
	t.Run("tampered signature", func(t *testing.T) {
		f := newOIDCFixture(t)
		app := f.application(t)
		token := f.sign(t, f.claims("nonce"), nil)
		parts := strings.Split(token, ".")
		sig, _ := base64.RawURLEncoding.DecodeString(parts[2])
		sig[0] ^= 1
		parts[2] = base64.RawURLEncoding.EncodeToString(sig)
		_, err := app.VerifyIDToken(t.Context(), strings.Join(parts, "."), TSAuthIDTokenVerificationOptions{Nonce: "nonce"})
		assertOIDCCode(t, err, "invalid_signature")
	})
	t.Run("duplicate claim", func(t *testing.T) {
		f := newOIDCFixture(t)
		app := f.application(t)
		body, _ := json.Marshal(f.claims("nonce"))
		body = append(body[:len(body)-1], []byte(`,"iss":"other"}`)...)
		_, err := app.VerifyIDToken(t.Context(), f.signBytes(t, body, nil), TSAuthIDTokenVerificationOptions{Nonce: "nonce"})
		assertOIDCCode(t, err, "invalid_token_json")
	})
}

func TestTSAuthOIDCDiscoveryMixupDenied(t *testing.T) {
	for _, field := range []string{"issuer", "endpoint"} {
		t.Run(field, func(t *testing.T) {
			f := newOIDCFixture(t)
			app := f.application(t)
			if field == "issuer" {
				f.document.Issuer = "https://other.example/acme"
			} else {
				f.document.JwksURI = "http://127.0.0.1/private"
			}
			_, err := app.Discover(t.Context())
			if err == nil {
				t.Fatal("discovery mix-up accepted")
			}
		})
	}
	f := newOIDCFixture(t)
	oidc := newTSAuthTestInstance(t, Config{TSAuthBaseURL: f.server.URL}).OIDC
	_, err := oidc.NewApplication(TSAuthOIDCApplicationConfig{Issuer: "https://api.terrascale.tech/acme", Authentication: TSAuthOAuthClientAuthentication{ClientID: "client", Method: TSAuthClientAuthenticationNone}, RedirectURI: "https://app.example/cb"})
	if err == nil {
		t.Fatal("test API proxy admitted")
	}
}

func TestTSAuthOIDCUncertainMutationNotRetried(t *testing.T) {
	f := newOIDCFixture(t)
	app := f.application(t)
	f.formHandler = func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(http.StatusServiceUnavailable)
		io.WriteString(w, `{"error":"temporarily_unavailable"}`)
	}
	_, err := app.Refresh(t.Context(), "refresh")
	var failure *TSAuthOIDCError
	if !errors.As(err, &failure) || !failure.Uncertain || f.requests != 1 {
		t.Fatalf("error=%v requests=%d", err, f.requests)
	}
	f.formHandler = func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(http.StatusBadRequest)
		io.WriteString(w, `{"error":"invalid_grant"}`)
	}
	_, err = app.Refresh(t.Context(), "refresh")
	if !errors.As(err, &failure) || failure.Uncertain || failure.Code != "invalid_grant" {
		t.Fatalf("deterministic error=%v", err)
	}
}

func TestTSAuthOIDCPrecancelledRequest(t *testing.T) {
	f := newOIDCFixture(t)
	app := f.application(t)
	ctx, cancel := context.WithCancel(t.Context())
	cancel()
	_, err := app.Discover(ctx)
	if err == nil || f.requests != 0 {
		t.Fatal("cancelled request proceeded")
	}
}

func TestTSAuthOIDCCanonicalIssuerTransport(t *testing.T) {
	f := newOIDCFixture(t)
	f.issuer = "https://auth.terrascale.tech/acme"
	f.document.Issuer = f.issuer
	f.document.AuthorizationEndpoint = f.issuer + "/oauth/authorize"
	f.document.TokenEndpoint = f.issuer + "/oauth/token"
	f.document.UserinfoEndpoint = f.issuer + "/oauth/userinfo"
	f.document.JwksURI = f.issuer + "/.well-known/jwks.json"
	f.document.RevocationEndpoint = f.issuer + "/oauth/revoke"
	f.document.EndSessionEndpoint = f.issuer + "/oauth/logout"
	local, _ := url.Parse(f.server.URL)
	var destinations []string
	transport := pinningRoundTripperFunc(func(request *http.Request) (*http.Response, error) {
		destinations = append(destinations, request.URL.Host)
		if request.URL.Host != "auth.terrascale.tech" {
			t.Errorf("unadmitted origin %s", request.URL.Host)
		}
		clone := request.Clone(request.Context())
		clone.URL.Scheme = local.Scheme
		clone.URL.Host = local.Host
		return http.DefaultTransport.RoundTrip(clone)
	})
	client, err := NewClient(Config{HTTPClient: &http.Client{Transport: transport}})
	if err != nil {
		t.Fatal(err)
	}
	instance, err := client.TSAuth.Instance("acme")
	if err != nil {
		t.Fatal(err)
	}
	app, err := instance.OIDC.NewApplication(TSAuthOIDCApplicationConfig{Issuer: f.issuer, Authentication: TSAuthOAuthClientAuthentication{ClientID: "client", Method: TSAuthClientAuthenticationNone}, RedirectURI: "https://app.example/callback"})
	if err != nil {
		t.Fatal(err)
	}
	attempt, err := app.BeginAuthorization(t.Context(), []string{"openid"})
	if err != nil {
		t.Fatal(err)
	}
	endpoint, _ := url.Parse(attempt.AuthorizationURL)
	if endpoint.Host != "auth.terrascale.tech" || attempt.Issuer != f.issuer {
		t.Fatal("hosted browser origin or issuer changed")
	}
	f.token = f.sign(t, f.claims(attempt.Nonce), nil)
	identity, err := app.VerifyIDToken(t.Context(), f.token, TSAuthIDTokenVerificationOptions{Nonce: attempt.Nonce, AccessToken: "access"})
	if err != nil || identity.Issuer != f.issuer {
		t.Fatalf("identity=%+v error=%v", identity, err)
	}
	for _, host := range destinations {
		if host != "auth.terrascale.tech" {
			t.Fatalf("unexpected origin %s", host)
		}
	}
}

func TestTSAuthOIDCGlobalIssuer(t *testing.T) {
	f := newOIDCFixture(t)
	local := strings.TrimPrefix(f.server.URL, "http://")
	f.issuer = "https://auth.terrascale.tech/acme"
	raw, _ := json.Marshal(f.document)
	json.Unmarshal([]byte(strings.ReplaceAll(string(raw), f.server.URL+"/acme", f.issuer)), &f.document)
	hosts := map[string]bool{}
	client, err := NewClient(Config{HTTPClient: &http.Client{Transport: pinningRoundTripperFunc(func(request *http.Request) (*http.Response, error) {
		hosts[request.URL.Host] = true
		clone := request.Clone(request.Context())
		clone.URL.Scheme, clone.URL.Host = "http", local
		return http.DefaultTransport.RoundTrip(clone)
	})}})
	if err != nil {
		t.Fatal(err)
	}
	instance, err := client.TSAuth.Instance("acme")
	if err != nil {
		t.Fatal(err)
	}
	app, err := instance.OIDC.NewApplication(TSAuthOIDCApplicationConfig{Issuer: f.issuer, Authentication: TSAuthOAuthClientAuthentication{ClientID: "client", Method: TSAuthClientAuthenticationNone}, RedirectURI: "https://app.example/callback"})
	if err != nil {
		t.Fatal(err)
	}
	attempt, err := app.BeginAuthorization(t.Context(), []string{"openid"})
	if err != nil {
		t.Fatal(err)
	}
	if !strings.HasPrefix(attempt.AuthorizationURL, f.issuer+"/oauth/authorize?") {
		t.Fatalf("authorization URL %s", attempt.AuthorizationURL)
	}
	f.token = f.sign(t, f.claims(attempt.Nonce), nil)
	if identity, err := app.VerifyIDToken(t.Context(), f.token, TSAuthIDTokenVerificationOptions{Nonce: attempt.Nonce, AccessToken: "access"}); err != nil || identity.Issuer != f.issuer {
		t.Fatalf("identity=%+v error=%v", identity, err)
	}
	if err := app.Revoke(t.Context(), "opaque"); err != nil {
		t.Fatal(err)
	}
	if f.requests != 1 || !reflect.DeepEqual(slices.Sorted(maps.Keys(hosts)), []string{"auth.terrascale.tech"}) {
		t.Fatalf("requests=%d hosts=%v", f.requests, hosts)
	}
}

func TestTSAuthOIDCIssuerMustBeGlobalOrigin(t *testing.T) {
	for _, tc := range []struct{ base, issuer string }{
		{"https://auth.terrascale.tech", "https://evil.example/acme"},
		{"https://auth.terrascale.tech", "https://gru.auth.terrascale.tech/acme"},
		{"https://auth.terrascale.tech", "https://auth.terrascale.tech.evil.example/acme"},
		{"https://auth.terrascale.tech", "https://AUTH.terrascale.tech/acme"},
		{"https://auth.terrascale.tech", "https://auth.terrascale.tech:8443/acme"},
		{"https://auth.example.com", "https://auth.terrascale.tech/acme"},
	} {
		instance := newTSAuthTestInstance(t, Config{TSAuthBaseURL: tc.base})
		_, err := instance.OIDC.NewApplication(TSAuthOIDCApplicationConfig{Issuer: tc.issuer, Authentication: TSAuthOAuthClientAuthentication{ClientID: "client", Method: TSAuthClientAuthenticationNone}, RedirectURI: "https://app.example/callback"})
		assertOIDCCode(t, err, "issuer_transport_mismatch")
	}
}

func TestTSAuthOIDCExactSecurityJSON(t *testing.T) {
	var tokens TSAuthTokenResponse
	if err := tokens.decodeHTTPResponse(200, []byte(`{"access_token":"a","access_token":"b","token_type":"Bearer","expires_in":60}`)); err == nil {
		t.Fatal("duplicate access_token admitted")
	}
	var document TSAuthDiscoveryDocument
	if err := document.decodeHTTPResponse(200, []byte("{\"issuer\":\"\xff\"}")); err == nil {
		t.Fatal("invalid UTF-8 issuer admitted")
	}
	if err := document.decodeHTTPResponse(200, []byte(`{"issuer":"wrong","ISSUER":"trusted"}`)); err != nil {
		t.Fatal(err)
	}
	if document.Issuer != "wrong" {
		t.Fatal("case alias changed exact issuer")
	}
	f := newOIDCFixture(t)
	app := f.application(t)
	claims := f.claims("nonce")
	claims["token_use"] = "access"
	claims["TOKEN_USE"] = "id"
	_, err := app.VerifyIDToken(t.Context(), f.sign(t, claims, nil), TSAuthIDTokenVerificationOptions{Nonce: "nonce"})
	assertOIDCCode(t, err, "invalid_identity_claims")
	for _, member := range []string{`"d":""`, `"d":null`, `"key_ops":[]`, `"key_ops":null`} {
		t.Run(member, func(t *testing.T) {
			key := f.jwks.Keys[0]
			raw, _ := json.Marshal(key)
			raw = append(raw[:len(raw)-1], []byte(","+member+"}")...)
			var keys TSAuthJWKS
			if err := keys.decodeHTTPResponse(200, append(append([]byte(`{"keys":[`), raw...), []byte(`]}`)...)); err != nil {
				t.Fatal(err)
			}
			candidate := keys.Keys[0]
			if !candidate.hasPrivateMember && !(candidate.hasKeyOps && !slices.Contains(candidate.KeyOps, "verify")) {
				t.Fatal("private or empty explicit key operations admitted")
			}
		})
	}
}

func TestTSAuthOIDCSuccessfulMutationMalformedResponseUncertain(t *testing.T) {
	f := newOIDCFixture(t)
	app := f.application(t)
	f.formHandler = func(w http.ResponseWriter, r *http.Request) {
		io.WriteString(w, `{"access_token":"a","access_token":"b"}`)
	}
	_, err := app.Refresh(t.Context(), "refresh")
	var failure *TSAuthOIDCError
	if !errors.As(err, &failure) || !failure.Uncertain || f.requests != 1 {
		t.Fatalf("duplicate successful mutation response: err=%v requests=%d", err, f.requests)
	}
	f.formHandler = func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Length", strconv.FormatInt(MaxResponseBodyBytes+1, 10))
		w.WriteHeader(200)
	}
	_, err = app.Refresh(t.Context(), "refresh")
	if !errors.As(err, &failure) || !failure.Uncertain || f.requests != 2 {
		t.Fatalf("oversized successful mutation response: err=%v requests=%d", err, f.requests)
	}
}

func TestTSAuthOIDCResponseStatusAndUserInfoDenials(t *testing.T) {
	var user TSAuthUserInfo
	if err := user.decodeHTTPResponse(200, []byte(`{"sub":"a","sub":"b"}`)); err == nil {
		t.Fatal("duplicate userinfo admitted")
	}
	if err := user.decodeHTTPResponse(200, []byte(`{"sub":"01ARZ3NDEKTSV4RRFFQ69G5FAC","SUB":"other"}`)); err != nil || user.Sub != "01ARZ3NDEKTSV4RRFFQ69G5FAC" {
		t.Fatal("userinfo exact field changed")
	}
	for _, status := range []int{201, 202, 204} {
		if err := user.decodeHTTPResponse(status, []byte(`{"sub":"user"}`)); err == nil {
			t.Fatal("wrong userinfo status admitted")
		}
		var token TSAuthTokenResponse
		if err := token.decodeHTTPResponse(status, []byte(`{"access_token":"a"}`)); err == nil {
			t.Fatal("wrong token status admitted")
		}
	}
}
