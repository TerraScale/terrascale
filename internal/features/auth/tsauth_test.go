package auth

import (
	"crypto"
	"crypto/rand"
	"crypto/rsa"
	"crypto/sha256"
	"encoding/base64"
	"encoding/json/v2"
	"errors"
	"math/big"
	"net/http"
	"net/http/httptest"
	"net/url"
	"os"
	"strings"
	"sync"
	"testing"
	"time"

	"golang.org/x/oauth2"
	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/core/memstore"
	"terrascale.tech/dashboard/internal/platform/csrf"
	"terrascale.tech/dashboard/internal/platform/render"
	"terrascale.tech/dashboard/internal/platform/session"
	"terrascale.tech/dashboard/internal/platform/throttle"
)

const (
	testClientID     = "dashboard"
	testClientSecret = "dashboard-secret"
	testRedirectURL  = "http://dashboard.test/login/callback"
	testSubject      = "user_ada"
)

// testIssuer is an in-process ts-auth: discovery, JWKS, and a token endpoint
// that checks client authentication, the redirect URI and PKCE, and rotates
// refresh tokens. Tests approve sign-ins with approve, standing in for the
// browser at the authorization endpoint.
type testIssuer struct {
	server *httptest.Server
	key    *rsa.PrivateKey

	mu sync.Mutex
	// claims shape every ID token the issuer signs; signer, when set, signs
	// them with a key the JWKS does not publish.
	claims     func(map[string]any)
	signer     *rsa.PrivateKey
	codes      map[string]issuedCode
	refreshes  map[string]string
	refreshed  int
	endSession bool
	pkce       []string
	// keepRefresh answers refresh grants without a new refresh token, so the
	// one presented stays valid.
	keepRefresh bool
	// headless is the fake headless API (native_test.go).
	headless   *fakeHeadless
	revocation bool
	// legacy leaves the headless grants out of discovery, as ts-auth did
	// before TS-582.
	legacy bool
	// discoveryGate, when set, holds discovery answers until it closes,
	// after reporting each on discoveryEntered.
	discoveryEntered chan struct{}
	discoveryGate    chan struct{}
}

type issuedCode struct {
	challenge, nonce, redirectURI string
}

func newTestIssuer(t *testing.T) *testIssuer {
	t.Helper()
	key, err := rsa.GenerateKey(rand.Reader, 2048)
	if err != nil {
		t.Fatal(err)
	}
	issuer := &testIssuer{
		key: key, codes: map[string]issuedCode{}, refreshes: map[string]string{},
		endSession: true, revocation: true, pkce: []string{"S256"}, claims: func(map[string]any) {},
	}
	mux := http.NewServeMux()
	mux.HandleFunc("GET /.well-known/openid-configuration", issuer.discovery)
	mux.HandleFunc("GET /keys", issuer.keys)
	mux.HandleFunc("POST /token", issuer.token)
	issuer.mountHeadless(mux)
	issuer.server = httptest.NewServer(mux)
	t.Cleanup(issuer.server.Close)
	return issuer
}

func (i *testIssuer) url() string { return i.server.URL }

func (i *testIssuer) discovery(w http.ResponseWriter, _ *http.Request) {
	i.mu.Lock()
	entered, gate := i.discoveryEntered, i.discoveryGate
	i.mu.Unlock()
	if gate != nil {
		entered <- struct{}{}
		<-gate
	}
	i.mu.Lock()
	defer i.mu.Unlock()
	document := map[string]any{
		"issuer": i.url(), "authorization_endpoint": i.url() + "/authorize", "token_endpoint": i.url() + "/token",
		"jwks_uri": i.url() + "/keys", "id_token_signing_alg_values_supported": []string{"RS256"},
		"response_types_supported": []string{"code"}, "subject_types_supported": []string{"public"},
		"code_challenge_methods_supported": i.pkce,
	}
	if i.endSession {
		document["end_session_endpoint"] = i.url() + "/logout?ui=1"
	}
	if i.revocation {
		document["revocation_endpoint"] = i.url() + "/revoke"
	}
	if !i.legacy {
		document["grant_types_supported"] = []string{"authorization_code", "refresh_token", passwordGrant, emailCodeGrant}
	}
	writeJSON(w, http.StatusOK, document)
}

func (i *testIssuer) keys(w http.ResponseWriter, _ *http.Request) {
	writeJSON(w, http.StatusOK, map[string]any{"keys": []map[string]string{{
		"kty": "RSA", "kid": "test-key", "alg": "RS256", "use": "sig",
		"n": base64.RawURLEncoding.EncodeToString(i.key.N.Bytes()),
		"e": base64.RawURLEncoding.EncodeToString(big.NewInt(int64(i.key.E)).Bytes()),
	}}})
}

// approve answers an authorization request the way ts-auth would after the
// user signs in, returning the authorization code.
func (i *testIssuer) approve(t *testing.T, authorizeURL string) (code, state string) {
	t.Helper()
	u, err := url.Parse(authorizeURL)
	if err != nil || !strings.HasPrefix(authorizeURL, i.url()+"/authorize?") {
		t.Fatalf("sign-in redirected to %q, want the issuer's authorization endpoint", authorizeURL)
	}
	query := u.Query()
	if query.Get("response_type") != "code" || query.Get("client_id") != testClientID || query.Get("code_challenge_method") != "S256" ||
		query.Get("code_challenge") == "" || query.Get("nonce") == "" || query.Get("state") == "" || query.Get("redirect_uri") != testRedirectURL {
		t.Fatalf("authorization request = %v", query)
	}
	if scope := " " + query.Get("scope") + " "; !strings.Contains(scope, " openid ") || !strings.Contains(scope, " offline_access ") {
		t.Fatalf("scopes = %q, want openid and offline_access", query.Get("scope"))
	}
	code = rand.Text()
	i.mu.Lock()
	i.codes[code] = issuedCode{challenge: query.Get("code_challenge"), nonce: query.Get("nonce"), redirectURI: query.Get("redirect_uri")}
	i.mu.Unlock()
	return code, query.Get("state")
}

func (i *testIssuer) token(w http.ResponseWriter, r *http.Request) {
	clientID, secret, ok := r.BasicAuth()
	if !ok {
		clientID, secret = r.PostFormValue("client_id"), r.PostFormValue("client_secret")
	}
	if clientID != testClientID || secret != testClientSecret {
		writeJSON(w, http.StatusUnauthorized, map[string]string{"error": "invalid_client"})
		return
	}
	i.mu.Lock()
	defer i.mu.Unlock()
	var subject, nonce string
	switch r.PostFormValue("grant_type") {
	case "authorization_code":
		issued, found := i.codes[r.PostFormValue("code")]
		delete(i.codes, r.PostFormValue("code"))
		verifier := sha256.Sum256([]byte(r.PostFormValue("code_verifier")))
		if !found || issued.redirectURI != r.PostFormValue("redirect_uri") || base64.RawURLEncoding.EncodeToString(verifier[:]) != issued.challenge {
			writeJSON(w, http.StatusBadRequest, map[string]string{"error": "invalid_grant"})
			return
		}
		subject, nonce = testSubject, issued.nonce
	case "refresh_token":
		owner, found := i.refreshes[r.PostFormValue("refresh_token")]
		if !found {
			writeJSON(w, http.StatusBadRequest, map[string]string{"error": "invalid_grant"})
			return
		}
		i.refreshed++
		if i.keepRefresh {
			writeJSON(w, http.StatusOK, map[string]any{
				"access_token": rand.Text(), "token_type": "Bearer", "expires_in": 3600, "id_token": i.idToken(owner, ""),
			})
			return
		}
		delete(i.refreshes, r.PostFormValue("refresh_token"))
		subject = owner
	case passwordGrant, emailCodeGrant:
		i.headlessGrant(w, r)
		return
	default:
		writeJSON(w, http.StatusBadRequest, map[string]string{"error": "unsupported_grant_type"})
		return
	}
	refresh := rand.Text()
	i.refreshes[refresh] = subject
	writeJSON(w, http.StatusOK, map[string]any{
		"access_token": rand.Text(), "token_type": "Bearer", "expires_in": 3600,
		"refresh_token": refresh, "id_token": i.idToken(subject, nonce),
	})
}

// idToken signs an RS256 ID token; i.mu is held.
func (i *testIssuer) idToken(subject, nonce string) string {
	now := time.Now()
	claims := map[string]any{
		"iss": i.url(), "aud": testClientID, "sub": subject, "iat": now.Unix(), "exp": now.Add(time.Hour).Unix(),
		"email": "ada@example.test", "email_verified": true, "name": "Ada Lovelace",
	}
	if nonce != "" {
		claims["nonce"] = nonce
	}
	i.claims(claims)
	return i.sign(claims)
}

// sign signs claims as an RS256 JWT; i.mu is held.
func (i *testIssuer) sign(claims map[string]any) string {
	header, _ := json.Marshal(map[string]string{"alg": "RS256", "kid": "test-key", "typ": "JWT"})
	payload, _ := json.Marshal(claims)
	signed := base64.RawURLEncoding.EncodeToString(header) + "." + base64.RawURLEncoding.EncodeToString(payload)
	digest := sha256.Sum256([]byte(signed))
	key := i.key
	if i.signer != nil {
		key = i.signer
	}
	signature, err := rsa.SignPKCS1v15(rand.Reader, key, crypto.SHA256, digest[:])
	if err != nil {
		panic(err)
	}
	return signed + "." + base64.RawURLEncoding.EncodeToString(signature)
}

func (i *testIssuer) refreshCount() int {
	i.mu.Lock()
	defer i.mu.Unlock()
	return i.refreshed
}

func (i *testIssuer) set(change func(*testIssuer)) {
	i.mu.Lock()
	defer i.mu.Unlock()
	change(i)
}

// tsAuthDashboard is the auth feature signing in through a test issuer.
type tsAuthDashboard struct {
	mux      *http.ServeMux
	store    *memstore.Store
	records  *session.MemoryStore
	sessions *session.Manager
	cookies  map[string]*http.Cookie
	tsAuth   *TSAuth
	deps     core.Deps
}

// newTSAuthDashboard mounts the auth feature for issuer; wrap, when given,
// wraps the store the feature sees.
func newTSAuthDashboard(t *testing.T, issuer *testIssuer, wrap ...func(core.Store) core.Store) *tsAuthDashboard {
	t.Helper()
	tsAuth, err := NewTSAuth(TSAuthConfig{
		Issuer: issuer.url(), ClientID: testClientID, ClientSecret: testClientSecret, RedirectURL: testRedirectURL,
	})
	if err != nil {
		t.Fatal(err)
	}
	store, records := memstore.New(), session.NewMemoryStore()
	sessions := session.New(store, session.Options{Store: records, Refresh: tsAuth.RefreshSession, Revoke: tsAuth.RevokeSession, UserToken: AccessToken})
	guard := csrf.New(sessions, csrf.Options{})
	var featureStore core.Store = store
	for _, w := range wrap {
		featureStore = w(featureStore)
	}
	deps := core.Deps{
		Store: featureStore, Sessions: sessions, CSRF: guard, Render: render.New(store, guard, nil, sessions.NoticeScope, false),
		Throttle: throttle.New(throttle.Options{}),
	}
	mux := http.NewServeMux()
	Mount(mux, deps, tsAuth)
	return &tsAuthDashboard{mux: mux, store: store, records: records, sessions: sessions, cookies: map[string]*http.Cookie{}, tsAuth: tsAuth, deps: deps}
}

// do sends a request with the browser's cookies and keeps the ones the
// response sets.
func (d *tsAuthDashboard) do(method, target string) *httptest.ResponseRecorder {
	request := httptest.NewRequest(method, "http://dashboard.test"+target, nil)
	for _, cookie := range d.cookies {
		request.AddCookie(cookie)
	}
	response := httptest.NewRecorder()
	d.mux.ServeHTTP(response, request)
	for _, cookie := range response.Result().Cookies() {
		if cookie.MaxAge < 0 {
			delete(d.cookies, cookie.Name)
		} else {
			d.cookies[cookie.Name] = cookie
		}
	}
	return response
}

// signIn runs the browser through /login and ts-auth back to the callback and
// returns the callback's response.
func (d *tsAuthDashboard) signIn(t *testing.T, issuer *testIssuer, login string) *httptest.ResponseRecorder {
	t.Helper()
	response := d.do(http.MethodGet, login)
	if response.Code != http.StatusSeeOther {
		t.Fatalf("GET %s = %d %s, want a redirect to ts-auth", login, response.Code, response.Body)
	}
	code, state := issuer.approve(t, response.Header().Get("Location"))
	return d.do(http.MethodGet, "/login/callback?"+url.Values{"code": {code}, "state": {state}}.Encode())
}

// record returns the stored session behind the browser's session cookie.
func (d *tsAuthDashboard) record(t *testing.T) (session.Record, [32]byte) {
	t.Helper()
	cookie, ok := d.cookies["ts_dashboard_session"]
	if !ok {
		t.Fatal("browser has no session cookie")
	}
	key := sha256.Sum256([]byte(cookie.Value))
	record, err := d.records.Get(t.Context(), key, time.Now())
	if err != nil {
		t.Fatal(err)
	}
	return record, key
}

func (d *tsAuthDashboard) signedIn(t *testing.T) bool {
	t.Helper()
	return d.do(http.MethodGet, "/api/v1/auth/me").Code == http.StatusOK
}

func TestTSAuthSignInCreatesLinkedAccount(t *testing.T) {
	t.Parallel()
	issuer := newTestIssuer(t)
	dashboard := newTSAuthDashboard(t, issuer)

	response := dashboard.signIn(t, issuer, "/login/sso?returnTo="+url.QueryEscape("/databases?tab=all"))
	if response.Code != http.StatusSeeOther || response.Header().Get("Location") != "/databases?tab=all" {
		t.Fatalf("callback = %d %q, want the return path", response.Code, response.Header().Get("Location"))
	}
	if _, kept := dashboard.cookies["ts_dashboard_signin"]; kept {
		t.Fatal("sign-in flow cookie outlived the callback")
	}
	account, err := dashboard.store.FindBySubject(t.Context(), testSubject)
	if err != nil || account.User.Email != "ada@example.test" || account.User.Name != "Ada Lovelace" {
		t.Fatalf("account = %+v, %v; want a new account for the ts-auth user", account.User, err)
	}
	if VerifyPassword(account.User.PasswordHash, "") {
		t.Fatal("ts-auth account has a usable password")
	}
	if !dashboard.signedIn(t) {
		t.Fatal("callback did not start a session")
	}
	if record, _ := dashboard.record(t); !strings.Contains(record.Refresh, testSubject) || record.RefreshAt.Before(time.Now().Add(minRecheck-time.Minute)) {
		t.Fatalf("session refresh state = %q at %v, want the subject's refresh token", record.Refresh, record.RefreshAt)
	}

	// Signing in again from another browser reuses the linked account, and
	// without a return path an account in setup continues setup.
	dashboard.cookies = map[string]*http.Cookie{}
	response = dashboard.signIn(t, issuer, "/login/sso")
	if location := response.Header().Get("Location"); location != "/onboarding" {
		t.Fatalf("callback location = %q, want /onboarding for an account in setup", location)
	}
	if again, err := dashboard.store.FindBySubject(t.Context(), testSubject); err != nil || again.ID != account.ID {
		t.Fatalf("second sign-in account = %v, %v; want %v", again.ID, err, account.ID)
	}
}

func TestTSAuthSignInLinksVerifiedEmail(t *testing.T) {
	t.Parallel()
	issuer := newTestIssuer(t)
	dashboard := newTSAuthDashboard(t, issuer)
	existing, err := dashboard.store.CreateAccount(t.Context(), core.User{Name: "Ada", Email: "ADA@example.test", PasswordHash: "unused"})
	if err != nil {
		t.Fatal(err)
	}

	issuer.set(func(i *testIssuer) { i.claims = func(c map[string]any) { c["email_verified"] = false } })
	if response := dashboard.signIn(t, issuer, "/login/sso"); response.Code != http.StatusForbidden || !strings.Contains(response.Body.String(), "must be verified") {
		t.Fatalf("unverified email callback = %d, want 403 asking for a verified email", response.Code)
	}
	if _, err := dashboard.store.FindBySubject(t.Context(), testSubject); !errors.Is(err, core.ErrNotFound) {
		t.Fatalf("unverified email linked an account: %v", err)
	}

	issuer.set(func(i *testIssuer) { i.claims = func(map[string]any) {} })
	if response := dashboard.signIn(t, issuer, "/login/sso"); response.Code != http.StatusSeeOther {
		t.Fatalf("verified email callback = %d %s", response.Code, response.Body)
	}
	linked, err := dashboard.store.FindBySubject(t.Context(), testSubject)
	if err != nil || linked.ID != existing.ID {
		t.Fatalf("linked account = %v, %v; want the account registered with the email", linked.ID, err)
	}
}

func TestTSAuthSignInRefusesEmailLinkedToAnotherSubject(t *testing.T) {
	t.Parallel()
	issuer := newTestIssuer(t)
	dashboard := newTSAuthDashboard(t, issuer)
	if _, err := dashboard.store.CreateAccount(t.Context(), core.User{
		Name: "Ada", Email: "ada@example.test", PasswordHash: "unused", AuthSubject: "someone_else",
	}); err != nil {
		t.Fatal(err)
	}
	if response := dashboard.signIn(t, issuer, "/login/sso"); response.Code != http.StatusForbidden || dashboard.signedIn(t) {
		t.Fatalf("callback for an email linked elsewhere = %d, want 403 without a session", response.Code)
	}
}

func TestTSAuthCallbackRejectsForgedOrInvalidResponses(t *testing.T) {
	t.Parallel()
	other, err := rsa.GenerateKey(rand.Reader, 2048)
	if err != nil {
		t.Fatal(err)
	}
	for _, tc := range []struct {
		name   string
		issuer func(*testIssuer)
		state  func(string) string
		status int
	}{
		{name: "bad state", state: func(string) string { return "forged" }, status: http.StatusBadRequest},
		{name: "missing state", state: func(string) string { return "" }, status: http.StatusBadRequest},
		{name: "bad nonce", issuer: func(i *testIssuer) { i.claims = func(c map[string]any) { c["nonce"] = "replayed" } }, status: http.StatusUnauthorized},
		{name: "missing nonce", issuer: func(i *testIssuer) { i.claims = func(c map[string]any) { delete(c, "nonce") } }, status: http.StatusUnauthorized},
		{name: "expired", issuer: func(i *testIssuer) {
			i.claims = func(c map[string]any) { c["exp"] = time.Now().Add(-time.Minute).Unix() }
		}, status: http.StatusUnauthorized},
		{name: "audience mismatch", issuer: func(i *testIssuer) { i.claims = func(c map[string]any) { c["aud"] = "another-client" } }, status: http.StatusUnauthorized},
		{name: "issuer mismatch", issuer: func(i *testIssuer) { i.claims = func(c map[string]any) { c["iss"] = "https://evil.example" } }, status: http.StatusUnauthorized},
		{name: "unpublished key", issuer: func(i *testIssuer) { i.signer = other }, status: http.StatusUnauthorized},
		{name: "no subject", issuer: func(i *testIssuer) { i.claims = func(c map[string]any) { delete(c, "sub") } }, status: http.StatusUnauthorized},
	} {
		t.Run(tc.name, func(t *testing.T) {
			t.Parallel()
			issuer := newTestIssuer(t)
			if tc.issuer != nil {
				issuer.set(tc.issuer)
			}
			dashboard := newTSAuthDashboard(t, issuer)
			code, state := issuer.approve(t, dashboard.do(http.MethodGet, "/login/sso").Header().Get("Location"))
			if tc.state != nil {
				state = tc.state(state)
			}
			response := dashboard.do(http.MethodGet, "/login/callback?"+url.Values{"code": {code}, "state": {state}}.Encode())
			if response.Code != tc.status || dashboard.signedIn(t) {
				t.Fatalf("callback = %d, want %d without a session", response.Code, tc.status)
			}
			if _, err := dashboard.store.FindBySubject(t.Context(), testSubject); !errors.Is(err, core.ErrNotFound) {
				t.Fatalf("rejected callback created an account: %v", err)
			}
		})
	}
}

func TestTSAuthCallbackWithoutFlowOrAfterUse(t *testing.T) {
	t.Parallel()
	issuer := newTestIssuer(t)
	dashboard := newTSAuthDashboard(t, issuer)
	code, state := issuer.approve(t, dashboard.do(http.MethodGet, "/login/sso").Header().Get("Location"))
	callback := "/login/callback?" + url.Values{"code": {code}, "state": {state}}.Encode()

	stranger := newTSAuthDashboard(t, issuer)
	if response := stranger.do(http.MethodGet, callback); response.Code != http.StatusBadRequest {
		t.Fatalf("callback in another browser = %d, want 400", response.Code)
	}
	if response := dashboard.do(http.MethodGet, callback); response.Code != http.StatusSeeOther {
		t.Fatalf("callback = %d %s", response.Code, response.Body)
	}
	if response := dashboard.do(http.MethodGet, callback); response.Code != http.StatusBadRequest {
		t.Fatalf("replayed callback = %d, want 400", response.Code)
	}
}

func TestTSAuthCallbackReportsRefusedSignIn(t *testing.T) {
	t.Parallel()
	issuer := newTestIssuer(t)
	dashboard := newTSAuthDashboard(t, issuer)
	_, state := issuer.approve(t, dashboard.do(http.MethodGet, "/login/sso").Header().Get("Location"))
	response := dashboard.do(http.MethodGet, "/login/callback?"+url.Values{"error": {"access_denied"}, "state": {state}}.Encode())
	if response.Code != http.StatusUnauthorized || !strings.Contains(response.Body.String(), "ts-auth did not sign you in") {
		t.Fatalf("refused sign-in = %d, want 401 with an explanation", response.Code)
	}
}

func TestTSAuthLoginKeepsReturnPathOnTheDashboard(t *testing.T) {
	t.Parallel()
	issuer := newTestIssuer(t)
	for _, returnTo := range []string{"https://evil.example/", "//evil.example", `/\evil.example`} {
		dashboard := newTSAuthDashboard(t, issuer)
		response := dashboard.signIn(t, issuer, "/login/sso?returnTo="+url.QueryEscape(returnTo))
		if location := response.Header().Get("Location"); !strings.HasPrefix(location, "/") || strings.HasPrefix(location, "//") {
			t.Fatalf("returnTo %q signed in to %q, want a dashboard path", returnTo, location)
		}
	}
}

func TestTSAuthLoginForwardsPromptLogin(t *testing.T) {
	t.Parallel()
	issuer := newTestIssuer(t)
	dashboard := newTSAuthDashboard(t, issuer)
	location, _ := url.Parse(dashboard.do(http.MethodGet, "/login/sso?prompt=login").Header().Get("Location"))
	if location.Query().Get("prompt") != "login" {
		t.Fatalf("authorization request = %v, want prompt=login", location.Query())
	}
	location, _ = url.Parse(dashboard.do(http.MethodGet, "/login/sso?prompt=none").Header().Get("Location"))
	if location.Query().Has("prompt") {
		t.Fatalf("authorization request = %v, want no other prompt passed on", location.Query())
	}
}

func TestTSAuthLoginPageIsTheDashboards(t *testing.T) {
	t.Parallel()
	dashboard := newTSAuthDashboard(t, newTestIssuer(t))
	response := dashboard.do(http.MethodGet, "/login?prompt=login&returnTo=%2Fdatabases")
	body := response.Body.String()
	if response.Code != http.StatusOK || !strings.Contains(body, `action="/login"`) ||
		!strings.Contains(body, `href="/login/sso?prompt=login&amp;returnTo=%2Fdatabases"`) || !strings.Contains(body, `href="/login/code?returnTo=%2Fdatabases"`) {
		t.Fatalf("GET /login = %d, want the password form with code and single sign-on links: %s", response.Code, body)
	}
	if strings.Contains(body, "in-memory demo") {
		t.Fatal("ts-auth sign-in page shows the demo's disabled providers")
	}
	// The dashboard's own password store is not consulted with ts-auth.
	if response := dashboard.do(http.MethodPost, "/api/v1/auth/password/login"); response.Code != http.StatusNotFound {
		t.Fatalf("password API = %d, want 404", response.Code)
	}
}

func TestTSAuthUnavailableIssuer(t *testing.T) {
	t.Parallel()
	issuer := newTestIssuer(t)
	issuer.set(func(i *testIssuer) { i.pkce = nil })
	dashboard := newTSAuthDashboard(t, issuer)
	if response := dashboard.do(http.MethodGet, "/login/sso"); response.Code != http.StatusServiceUnavailable {
		t.Fatalf("login without PKCE support = %d, want 503", response.Code)
	}
	// Discovery is retried after a failure.
	issuer.set(func(i *testIssuer) { i.pkce = []string{"plain", "S256"} })
	if response := dashboard.do(http.MethodGet, "/login/sso"); response.Code != http.StatusSeeOther {
		t.Fatalf("login after ts-auth recovered = %d, want a redirect", response.Code)
	}

	issuer.server.Close()
	down := newTSAuthDashboard(t, issuer)
	if response := down.do(http.MethodGet, "/login/sso"); response.Code != http.StatusServiceUnavailable {
		t.Fatalf("login with ts-auth down = %d, want 503", response.Code)
	}
}

// expire makes the browser's session due for a recheck with ts-auth.
func (d *tsAuthDashboard) expire(t *testing.T) {
	t.Helper()
	record, key := d.record(t)
	record.RefreshAt = time.Now().Add(-time.Second)
	if err := d.records.Put(t.Context(), key, record); err != nil {
		t.Fatal(err)
	}
}

func TestTSAuthRefreshRechecksSession(t *testing.T) {
	t.Parallel()
	issuer := newTestIssuer(t)
	dashboard := newTSAuthDashboard(t, issuer)
	dashboard.signIn(t, issuer, "/login/sso")
	before, _ := dashboard.record(t)

	dashboard.expire(t)
	if !dashboard.signedIn(t) {
		t.Fatal("refreshed session was signed out")
	}
	after, _ := dashboard.record(t)
	if issuer.refreshCount() != 1 || after.Refresh == before.Refresh || after.RefreshAt.Before(time.Now()) {
		t.Fatalf("refreshes=%d refresh rotated=%v next=%v; want one refresh that rotated the token", issuer.refreshCount(), after.Refresh != before.Refresh, after.RefreshAt)
	}
	if !dashboard.signedIn(t) || issuer.refreshCount() != 1 {
		t.Fatalf("refreshes=%d, want no refresh before the next recheck", issuer.refreshCount())
	}

	// ts-auth revoked the user's tokens: the next recheck signs them out.
	issuer.set(func(i *testIssuer) { clear(i.refreshes) })
	dashboard.expire(t)
	if dashboard.signedIn(t) {
		t.Fatal("session outlived its revoked refresh token")
	}
}

// TestTSAuthRefreshKeepsTokenTSAuthDoesNotRotate covers refresh responses
// without refresh_token: the session keeps the token it has and stays
// rechecked with it.
func TestTSAuthRefreshKeepsTokenTSAuthDoesNotRotate(t *testing.T) {
	t.Parallel()
	issuer := newTestIssuer(t)
	dashboard := newTSAuthDashboard(t, issuer)
	dashboard.signIn(t, issuer, "/login/sso")
	before, _ := dashboard.record(t)
	issuer.set(func(i *testIssuer) { i.keepRefresh = true })
	for range 2 {
		dashboard.expire(t)
		if !dashboard.signedIn(t) {
			t.Fatal("session refreshed without a new refresh token was signed out")
		}
	}
	if after, _ := dashboard.record(t); refreshToken(t, after.Refresh) != refreshToken(t, before.Refresh) || issuer.refreshCount() != 2 {
		t.Fatalf("refreshes=%d kept=%v; want two refreshes with the same token", issuer.refreshCount(), after.Refresh == before.Refresh)
	}
	issuer.set(func(i *testIssuer) { clear(i.refreshes) })
	dashboard.expire(t)
	if dashboard.signedIn(t) {
		t.Fatal("session outlived its revoked, never-rotated refresh token")
	}
}

func TestRecheckAtFollowsTheAccessTokenExpiry(t *testing.T) {
	now := time.Now()
	if at := recheckAt(&oauth2.Token{Expiry: now.Add(2 * time.Minute)}); !at.Equal(now.Add(2 * time.Minute)) {
		t.Fatalf("recheck of a 2-minute token at %v, want its expiry", at)
	}
	if at := recheckAt(&oauth2.Token{Expiry: now.Add(time.Second)}); at.Before(now.Add(minRenewal - time.Second)) {
		t.Fatalf("recheck of a 1-second token at %v, want minRenewal later", at)
	}
	if at := recheckAt(&oauth2.Token{}); at.Before(now.Add(minRecheck - time.Second)) {
		t.Fatalf("recheck without an expiry at %v, want minRecheck later", at)
	}
}

func refreshToken(t *testing.T, refresh string) string {
	t.Helper()
	var state refreshState
	if err := json.Unmarshal([]byte(refresh), &state); err != nil {
		t.Fatal(err)
	}
	return state.Token
}

// TestTSAuthSessionKeepsTheAccessTokenForTerraBase checks the session hands
// out ts-auth's latest access token, which TerraBase's usage API accepts,
// and none once it expires.
func TestTSAuthSessionKeepsTheAccessTokenForTerraBase(t *testing.T) {
	t.Parallel()
	issuer := newTestIssuer(t)
	dashboard := newTSAuthDashboard(t, issuer)
	dashboard.signIn(t, issuer, "/login")
	userToken := func() string {
		request := httptest.NewRequest(http.MethodGet, "http://dashboard.test/", nil)
		for _, cookie := range dashboard.cookies {
			request.AddCookie(cookie)
		}
		return dashboard.sessions.UserToken(request)
	}
	first := userToken()
	if first == "" {
		t.Fatal("signed-in session has no access token")
	}
	dashboard.expire(t)
	if !dashboard.signedIn(t) {
		t.Fatal("refreshed session was signed out")
	}
	if second := userToken(); second == "" || second == first {
		t.Fatalf("access token after refresh = %q, want ts-auth's new one", second)
	}
	expired, err := json.Marshal(refreshState{Subject: testSubject, Token: "r", Access: "a", AccessExpiry: time.Now().Add(-time.Second)})
	if err != nil {
		t.Fatal(err)
	}
	if AccessToken(string(expired)) != "" || AccessToken("not json") != "" {
		t.Fatal("an expired or unreadable refresh state yielded an access token")
	}
}

func TestNewTSAuthRequiresCompleteRegistration(t *testing.T) {
	t.Parallel()
	complete := TSAuthConfig{Issuer: "https://auth.example", ClientID: testClientID, ClientSecret: testClientSecret, RedirectURL: testRedirectURL}
	if _, err := NewTSAuth(complete); err != nil {
		t.Fatal(err)
	}
	for name, change := range map[string]func(*TSAuthConfig){
		"issuer": func(c *TSAuthConfig) { c.Issuer = "" }, "client ID": func(c *TSAuthConfig) { c.ClientID = "" },
		"client secret": func(c *TSAuthConfig) { c.ClientSecret = "" }, "redirect URL": func(c *TSAuthConfig) { c.RedirectURL = "" },
		"relative redirect URL": func(c *TSAuthConfig) { c.RedirectURL = "/login/callback" },
	} {
		cfg := complete
		change(&cfg)
		if _, err := NewTSAuth(cfg); err == nil {
			t.Errorf("NewTSAuth without %s succeeded", name)
		}
	}
}

func TestTSAuthRefreshRejectsAnotherSubject(t *testing.T) {
	t.Parallel()
	issuer := newTestIssuer(t)
	dashboard := newTSAuthDashboard(t, issuer)
	dashboard.signIn(t, issuer, "/login/sso")
	issuer.set(func(i *testIssuer) { i.claims = func(c map[string]any) { c["sub"] = "someone_else" } })
	dashboard.expire(t)
	if dashboard.signedIn(t) {
		t.Fatal("session survived a refresh for another subject")
	}
}

func TestTSAuthRefreshKeepsSessionWhileTSAuthIsDown(t *testing.T) {
	t.Parallel()
	issuer := newTestIssuer(t)
	dashboard := newTSAuthDashboard(t, issuer)
	dashboard.signIn(t, issuer, "/login/sso")
	issuer.server.Close()
	dashboard.expire(t)
	if !dashboard.signedIn(t) {
		t.Fatal("ts-auth outage signed the user out")
	}
	if record, _ := dashboard.record(t); !record.RefreshAt.After(time.Now()) {
		t.Fatalf("next recheck at %v, want it postponed", record.RefreshAt)
	}
}

func TestTSAuthLogoutEndsTSAuthSession(t *testing.T) {
	t.Parallel()
	issuer := newTestIssuer(t)
	dashboard := newTSAuthDashboard(t, issuer)
	dashboard.signIn(t, issuer, "/login/sso")

	response := dashboard.do(http.MethodPost, "/api/v1/auth/logout")
	location, err := url.Parse(response.Header().Get("Location"))
	if response.Code != http.StatusSeeOther || err != nil || !strings.HasPrefix(location.String(), issuer.url()+"/logout?") {
		t.Fatalf("logout = %d %q, want ts-auth's end_session_endpoint", response.Code, response.Header().Get("Location"))
	}
	query := location.Query()
	if query.Get("ui") != "1" || query.Get("client_id") != testClientID || query.Get("post_logout_redirect_uri") != "http://dashboard.test/login" {
		t.Fatalf("end-session request = %v", query)
	}
	if dashboard.signedIn(t) {
		t.Fatal("logout left the dashboard session")
	}

	issuer.set(func(i *testIssuer) { i.endSession = false })
	without := newTSAuthDashboard(t, issuer)
	without.signIn(t, issuer, "/login/sso")
	if location := without.do(http.MethodPost, "/api/v1/auth/logout").Header().Get("Location"); location != "/login?prompt=login" {
		t.Fatalf("logout without end_session_endpoint = %q, want a sign-in that asks for credentials", location)
	}
	issuer.server.Close()
	down := newTSAuthDashboard(t, issuer)
	if location := down.do(http.MethodPost, "/api/v1/auth/logout").Header().Get("Location"); location != "/login?prompt=login" {
		t.Fatalf("logout with ts-auth down = %q, want a sign-in that asks for credentials", location)
	}
}

// TestTSAuthLiveIssuer checks the dashboard's client against a running ts-auth
// (TS-491) instead of the in-process issuer: discovery, the PKCE and
// end-session metadata the dashboard relies on, and the sign-in redirect.
func TestTSAuthLiveIssuer(t *testing.T) {
	issuerURL := os.Getenv("TS_AUTH_TEST_ISSUER")
	if issuerURL == "" {
		t.Skip("TS-491 has not published the ts-auth binary yet; run one and set TS_AUTH_TEST_ISSUER (and TS_AUTH_TEST_CLIENT_ID/SECRET) to test against it")
	}
	tsAuth, err := NewTSAuth(TSAuthConfig{
		Issuer: issuerURL, ClientID: os.Getenv("TS_AUTH_TEST_CLIENT_ID"), ClientSecret: os.Getenv("TS_AUTH_TEST_CLIENT_SECRET"),
		RedirectURL: testRedirectURL,
	})
	if err != nil {
		t.Fatal(err)
	}
	provider, err := tsAuth.discover(t.Context())
	if err != nil {
		t.Fatal(err)
	}
	if provider.endSession == "" {
		t.Error("ts-auth advertises no end_session_endpoint; signing out cannot end its session")
	}
	response := httptest.NewRecorder()
	if err := tsAuth.signIn(response, httptest.NewRequest(http.MethodGet, "/login", nil), "/"); err != nil {
		t.Fatal(err)
	}
	authorize, err := http.Get(response.Header().Get("Location"))
	if err != nil {
		t.Fatal(err)
	}
	authorize.Body.Close()
	if authorize.StatusCode >= http.StatusBadRequest {
		t.Fatalf("ts-auth authorization endpoint = %d for the dashboard's request", authorize.StatusCode)
	}
}
