package e2e

import (
	"crypto"
	"crypto/rand"
	"crypto/rsa"
	"crypto/sha256"
	"encoding/base64"
	"encoding/json/v2"
	"fmt"
	"math/big"
	"net/http"
	"net/http/httptest"
	"net/url"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/mxschmitt/playwright-go"

	"terrascale.tech/dashboard/internal/app"
	"terrascale.tech/dashboard/internal/platform/config"
)

// fakeTSAuth is ts-auth as the dashboard uses it after TS-582: the
// password and email-code grants and the JSON steps that send mail, plus
// the redirect flow single sign-on keeps. Its mailbox holds the last code
// each address was sent.
type fakeTSAuth struct {
	server *httptest.Server
	key    *rsa.PrivateKey

	mu        sync.Mutex
	passwords map[string]string
	subjects  map[string]string
	pending   map[string]string // sign-ups waiting for their code, by email
	mailbox   map[string]string
	resets    map[string]string
	grants    map[string]fakeGrant // authorization codes
	refreshes map[string]string
	tokens    map[string]string // pending sign-up tokens, by email
	// legacy leaves the headless grants out of discovery, as ts-auth did
	// before TS-582.
	legacy bool
}

type fakeGrant struct{ email, nonce, challenge string }

const (
	fakeClientID     = "ts-dashboard"
	fakeClientSecret = "e2e-dashboard-secret"
	fakeSSOEmail     = "sso-user@example.test"
)

func newFakeTSAuth(t *testing.T) *fakeTSAuth {
	t.Helper()
	key, err := rsa.GenerateKey(rand.Reader, 2048)
	must(t, err)
	f := &fakeTSAuth{
		key: key, passwords: map[string]string{}, subjects: map[string]string{}, pending: map[string]string{},
		mailbox: map[string]string{}, resets: map[string]string{}, grants: map[string]fakeGrant{}, refreshes: map[string]string{}, tokens: map[string]string{},
	}
	mux := http.NewServeMux()
	mux.HandleFunc("GET /.well-known/openid-configuration", func(w http.ResponseWriter, _ *http.Request) {
		base := f.server.URL
		grants := []string{"authorization_code", "refresh_token"}
		f.mu.Lock()
		if !f.legacy {
			grants = append(grants, "urn:terrascale:params:oauth:grant-type:password", "urn:terrascale:params:oauth:grant-type:email-code")
		}
		f.mu.Unlock()
		writeFakeJSON(w, http.StatusOK, map[string]any{
			"issuer": base, "authorization_endpoint": base + "/oauth/authorize", "token_endpoint": base + "/oauth/token",
			"jwks_uri": base + "/jwks.json", "end_session_endpoint": base + "/oauth/logout", "revocation_endpoint": base + "/oauth/revoke",
			"id_token_signing_alg_values_supported": []string{"RS256"}, "response_types_supported": []string{"code"},
			"subject_types_supported": []string{"public"}, "code_challenge_methods_supported": []string{"S256"},
			"grant_types_supported": grants,
		})
	})
	mux.HandleFunc("GET /jwks.json", func(w http.ResponseWriter, _ *http.Request) {
		writeFakeJSON(w, http.StatusOK, map[string]any{"keys": []map[string]string{{
			"kty": "RSA", "kid": "e2e", "alg": "RS256", "use": "sig",
			"n": base64.RawURLEncoding.EncodeToString(key.N.Bytes()),
			"e": base64.RawURLEncoding.EncodeToString(big.NewInt(int64(key.E)).Bytes()),
		}}})
	})
	// Single sign-on: the external provider approves fakeSSOEmail at once.
	mux.HandleFunc("GET /oauth/authorize", func(w http.ResponseWriter, r *http.Request) {
		query := r.URL.Query()
		code := rand.Text()
		f.mu.Lock()
		f.grants[code] = fakeGrant{email: fakeSSOEmail, nonce: query.Get("nonce"), challenge: query.Get("code_challenge")}
		f.mu.Unlock()
		http.Redirect(w, r, query.Get("redirect_uri")+"?"+url.Values{"code": {code}, "state": {query.Get("state")}}.Encode(), http.StatusSeeOther)
	})
	mux.HandleFunc("GET /oauth/logout", func(w http.ResponseWriter, r *http.Request) {
		http.Redirect(w, r, r.URL.Query().Get("post_logout_redirect_uri"), http.StatusSeeOther)
	})
	mux.HandleFunc("POST /oauth/token", f.token)
	mux.HandleFunc("POST /oauth/revoke", func(w http.ResponseWriter, r *http.Request) {
		f.mu.Lock()
		defer f.mu.Unlock()
		delete(f.refreshes, r.PostFormValue("token"))
	})
	mux.HandleFunc("POST /headless/v1/{step...}", f.headless)
	f.server = httptest.NewServer(mux)
	t.Cleanup(f.server.Close)
	return f
}

func writeFakeJSON(w http.ResponseWriter, status int, value any) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	_ = json.MarshalWrite(w, value)
}

// code returns the last code mailed to email.
func (f *fakeTSAuth) code(t *testing.T, email string) string {
	t.Helper()
	f.mu.Lock()
	defer f.mu.Unlock()
	code, ok := f.mailbox[email]
	if !ok {
		t.Fatalf("no code was mailed to %s", email)
	}
	return code
}

func (f *fakeTSAuth) mail(email string) string {
	code := fmt.Sprintf("%06d", time.Now().UnixNano()%1_000_000)
	f.mailbox[email] = code
	return code
}

func (f *fakeTSAuth) headless(w http.ResponseWriter, r *http.Request) {
	var fields map[string]string
	if id, secret, ok := r.BasicAuth(); !ok || id != fakeClientID || secret != fakeClientSecret || json.UnmarshalRead(r.Body, &fields) != nil || fields["ip_address"] == "" {
		writeFakeJSON(w, http.StatusBadRequest, map[string]any{"error": map[string]string{"code": "invalid_request", "message": "bad request"}})
		return
	}
	f.mu.Lock()
	defer f.mu.Unlock()
	email := fields["email"]
	_, exists := f.subjects[email]
	switch r.PathValue("step") {
	case "email-code/start":
		if exists {
			f.mail(email)
		}
	case "sign-up":
		token := rand.Text()
		if !exists {
			f.pending[email], f.tokens[email] = fields["password"], token
		}
		f.mail(email)
		writeFakeJSON(w, http.StatusAccepted, map[string]string{"pending_token": token})
		return
	case "password-reset/start":
		if exists {
			f.resets[email] = f.mail(email)
		}
	case "password-reset/complete":
		if code, ok := f.resets[email]; !ok || code != fields["code"] {
			writeFakeJSON(w, http.StatusBadRequest, map[string]any{"error": map[string]string{"code": "invalid_code", "message": "wrong code"}})
			return
		}
		delete(f.resets, email)
		f.passwords[email] = fields["new_password"]
		w.WriteHeader(http.StatusNoContent)
		return
	}
	w.WriteHeader(http.StatusAccepted)
}

func (f *fakeTSAuth) token(w http.ResponseWriter, r *http.Request) {
	if id, secret, ok := r.BasicAuth(); !ok || id != fakeClientID || secret != fakeClientSecret {
		writeFakeJSON(w, http.StatusUnauthorized, map[string]string{"error": "invalid_client"})
		return
	}
	f.mu.Lock()
	defer f.mu.Unlock()
	invalid := func() { writeFakeJSON(w, http.StatusBadRequest, map[string]string{"error": "invalid_grant"}) }
	email, nonce := r.PostFormValue("email"), ""
	switch r.PostFormValue("grant_type") {
	case "urn:terrascale:params:oauth:grant-type:password":
		if password, ok := f.passwords[email]; !ok || password != r.PostFormValue("password") {
			invalid()
			return
		}
	case "urn:terrascale:params:oauth:grant-type:email-code":
		if code, ok := f.mailbox[email]; !ok || code != r.PostFormValue("code") {
			invalid()
			return
		}
		if password, ok := f.pending[email]; ok {
			if r.PostFormValue("pending_token") != f.tokens[email] {
				invalid()
				return
			}
			delete(f.pending, email)
			f.subjects[email], f.passwords[email] = "user_"+rand.Text(), password
		}
		delete(f.mailbox, email)
	case "authorization_code":
		grant, ok := f.grants[r.PostFormValue("code")]
		delete(f.grants, r.PostFormValue("code"))
		verifier := sha256.Sum256([]byte(r.PostFormValue("code_verifier")))
		if !ok || base64.RawURLEncoding.EncodeToString(verifier[:]) != grant.challenge {
			invalid()
			return
		}
		email, nonce = grant.email, grant.nonce
		if _, ok := f.subjects[email]; !ok {
			f.subjects[email] = "user_" + rand.Text()
		}
	case "refresh_token":
		owner, ok := f.refreshes[r.PostFormValue("refresh_token")]
		delete(f.refreshes, r.PostFormValue("refresh_token"))
		if !ok {
			invalid()
			return
		}
		email = owner
	default:
		invalid()
		return
	}
	subject, ok := f.subjects[email]
	if !ok {
		invalid()
		return
	}
	now := time.Now()
	claims := map[string]any{
		"iss": f.server.URL, "aud": fakeClientID, "sub": subject, "iat": now.Unix(), "exp": now.Add(time.Hour).Unix(),
		"email": email, "email_verified": true,
	}
	if nonce != "" {
		claims["nonce"] = nonce
	}
	refresh := rand.Text()
	f.refreshes[refresh] = email
	writeFakeJSON(w, http.StatusOK, map[string]any{
		"access_token": rand.Text(), "token_type": "Bearer", "expires_in": 3600, "refresh_token": refresh, "id_token": f.sign(claims),
	})
}

func (f *fakeTSAuth) sign(claims map[string]any) string {
	header, _ := json.Marshal(map[string]string{"alg": "RS256", "kid": "e2e", "typ": "JWT"})
	payload, _ := json.Marshal(claims)
	signed := base64.RawURLEncoding.EncodeToString(header) + "." + base64.RawURLEncoding.EncodeToString(payload)
	digest := sha256.Sum256([]byte(signed))
	signature, err := rsa.SignPKCS1v15(rand.Reader, f.key, crypto.SHA256, digest[:])
	if err != nil {
		panic(err)
	}
	return signed + "." + base64.RawURLEncoding.EncodeToString(signature)
}

// tsAuthDashboard starts an in-process dashboard that signs users in through
// a fake ts-auth.
func tsAuthDashboard(t *testing.T) (string, *fakeTSAuth) {
	t.Helper()
	if testing.Short() {
		t.Skip("starts a second in-process dashboard with a fake ts-auth")
	}
	fake := newFakeTSAuth(t)
	var handler http.Handler
	dashboard := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { handler.ServeHTTP(w, r) }))
	t.Cleanup(dashboard.Close)
	application, err := app.New(config.Config{Demo: true, TSAuth: config.TSAuth{
		Issuer: fake.server.URL, ClientID: fakeClientID, ClientSecret: fakeClientSecret, RedirectURL: dashboard.URL + "/login/callback",
	}})
	must(t, err)
	handler = application.Handler
	return dashboard.URL, fake
}

// rejectingPage is a page whose rejected sign-in posts, answered with the
// form again under a 4xx status, are not console failures.
func rejectingPage(t *testing.T, base string) playwright.Page {
	t.Helper()
	return watchedPageExcept(t, newContext(t, base), func(message playwright.ConsoleMessage) bool {
		location := message.Location()
		if location == nil || !rejectedFormDocument.MatchString(message.Text()) {
			return false
		}
		parsed, err := url.Parse(location.URL)
		return err == nil && (parsed.Path == "/login" || parsed.Path == "/login/code/verify" || parsed.Path == "/login/callback")
	})
}

// signedInAs checks whose account the page's session opens.
func signedInAs(t *testing.T, page playwright.Page, email, name string) {
	t.Helper()
	var me struct {
		User struct {
			Email string `json:"email"`
			Name  string `json:"name"`
		} `json:"user"`
	}
	responseJSON(t, apiGet(t, page.Request(), "/api/v1/auth/me", ""), http.StatusOK, &me)
	if me.User.Email != email || me.User.Name != name {
		t.Fatalf("signed in as %+v, want %s (%s)", me.User, email, name)
	}
}

func signUpThroughTSAuth(t *testing.T, page playwright.Page, fake *fakeTSAuth, name, email, password string) {
	t.Helper()
	goTo(t, page, "/onboarding")
	form := page.Locator(`form[action="/onboarding"]`)
	fill(t, form.Locator("#displayName"), name)
	fill(t, form.Locator("#email"), email)
	fill(t, form.Locator("#password"), password)
	fill(t, form.Locator("#confirmPassword"), password)
	must(t, form.Locator("#terms").Check())
	click(t, byRole(form, *playwright.AriaRoleButton, "Create account"))
	must(t, expect.Locator(page.Locator("h1")).ToHaveText("Verify your email"))
	fill(t, page.Locator("#code"), fake.code(t, email))
	click(t, pageByRole(page, *playwright.AriaRoleButton, "Continue"))
	waitForPath(t, page, func(u *url.URL) bool { return u.Path == "/onboarding" })
}

func TestNativeSignUpAndPasswordSignIn(t *testing.T) {
	t.Parallel()
	base, fake := tsAuthDashboard(t)
	page := rejectingPage(t, base)
	email := strings.ToLower(uniqueName("native")) + "@example.test"
	signUpThroughTSAuth(t, page, fake, "Ada Native", email, "analytical engine 1")
	signedInAs(t, page, email, "Ada Native")

	logout(t, page)
	goTo(t, page, "/login")
	form := page.Locator(`form[action="/login"]`)
	fill(t, form.Locator(`[name="email"]`), email)
	fill(t, form.Locator(`[name="password"]`), "wrong password 1")
	click(t, byRole(form, *playwright.AriaRoleButton, "Sign in"))
	must(t, expect.Locator(page.Locator(".ts-alert-danger")).ToContainText("Incorrect email or password."))
	login(t, page, email, "analytical engine 1")
	if path := currentPath(t, page); path != "/onboarding" {
		t.Fatalf("password sign-in landed on %s, want account setup", path)
	}
}

func TestNativeEmailCodeSignIn(t *testing.T) {
	t.Parallel()
	base, fake := tsAuthDashboard(t)
	setup := watchedPage(t, newContext(t, base))
	email := strings.ToLower(uniqueName("code")) + "@example.test"
	signUpThroughTSAuth(t, setup, fake, "Grace Code", email, "compiler grace 1")

	page := rejectingPage(t, base)
	goTo(t, page, "/login")
	click(t, pageByRole(page, *playwright.AriaRoleLink, "Email me a code"))
	fill(t, page.Locator("#email"), email)
	click(t, pageByRole(page, *playwright.AriaRoleButton, "Email me a code"))
	must(t, expect.Locator(page.Locator("h1")).ToHaveText("Check your email"))
	fill(t, page.Locator("#code"), "000000")
	click(t, pageByRole(page, *playwright.AriaRoleButton, "Continue"))
	must(t, expect.Locator(page.Locator("#code-error")).ToContainText("That code is wrong or has expired."))
	screenshot(t, page, "native-code-wrong")
	fill(t, page.Locator("#code"), fake.code(t, email))
	click(t, pageByRole(page, *playwright.AriaRoleButton, "Continue"))
	waitForPath(t, page, func(u *url.URL) bool { return u.Path == "/onboarding" })
	signedInAs(t, page, email, "Grace Code")
}

func TestNativePasswordReset(t *testing.T) {
	t.Parallel()
	base, fake := tsAuthDashboard(t)
	page := watchedPage(t, newContext(t, base))
	email := strings.ToLower(uniqueName("reset")) + "@example.test"
	signUpThroughTSAuth(t, page, fake, "Reset Native", email, "first password 1")
	logout(t, page)

	click(t, pageByRole(page, *playwright.AriaRoleLink, "Forgot password"))
	fill(t, page.Locator("#email"), email)
	click(t, pageByRole(page, *playwright.AriaRoleButton, "Email me a code"))
	must(t, expect.Locator(page.Locator("h1")).ToHaveText("Choose a new password"))
	fill(t, page.Locator("#code"), fake.code(t, email))
	fill(t, page.Locator("#password"), "second password 2")
	fill(t, page.Locator("#confirmPassword"), "second password 2")
	click(t, pageByRole(page, *playwright.AriaRoleButton, "Change password"))
	waitForPath(t, page, func(u *url.URL) bool { return u.Path == "/login" })
	must(t, expect.Locator(page.Locator(".ts-alert-success")).ToContainText("Your password was changed."))
	screenshot(t, page, "native-reset-done")
	login(t, page, email, "second password 2")
	if path := currentPath(t, page); path != "/onboarding" {
		t.Fatalf("sign-in with the new password landed on %s", path)
	}
}

func TestNativeLoginKeepsSingleSignOnRedirect(t *testing.T) {
	t.Parallel()
	base, _ := tsAuthDashboard(t)
	page := rejectingPage(t, base)
	// A failed sign-in offers a way back that reaches the sign-in form,
	// instead of bouncing through ts-auth into the same failure.
	goTo(t, page, "/login/callback?state=stale&code=stale")
	must(t, expect.Locator(page.Locator(".ts-alert-warning")).ToContainText("Reference: "))
	screenshot(t, page, "native-sign-in-failed")
	click(t, pageByRole(page, *playwright.AriaRoleLink, "Try again"))
	waitForPath(t, page, func(u *url.URL) bool { return u.Path == "/login" })
	must(t, expect.Locator(page.Locator(`form[action="/login"]`)).ToBeVisible())
	goTo(t, page, "/login")
	screenshot(t, page, "native-login")
	// The sign-in column is narrowest just above the breakpoint that hides
	// the map; the text options still fit their buttons there.
	must(t, page.SetViewportSize(1040, 800))
	for _, name := range []string{"Email me a code", "Single sign-on"} {
		fits, err := pageByRole(page, *playwright.AriaRoleLink, name).Evaluate(`link => link.scrollWidth <= link.clientWidth`, nil)
		must(t, err)
		if fits != true {
			t.Fatalf("%q overflows its button in the narrowest sign-in column", name)
		}
	}
	click(t, pageByRole(page, *playwright.AriaRoleLink, "Single sign-on"))
	waitForPath(t, page, func(u *url.URL) bool { return u.Path == "/onboarding" })
	// ts-auth has no name for the user, so the account is named after the email.
	signedInAs(t, page, fakeSSOEmail, fakeSSOEmail)
}

func TestNativeLoginFallsBackToRedirectWithoutHeadlessAPI(t *testing.T) {
	t.Parallel()
	base, fake := tsAuthDashboard(t)
	fake.mu.Lock()
	fake.legacy = true
	fake.mu.Unlock()
	// Signing in and signing up both go through ts-auth's own page, which
	// approves the single sign-on user at once.
	page := watchedPage(t, newContext(t, base))
	goTo(t, page, "/onboarding")
	waitForPath(t, page, func(u *url.URL) bool { return u.Path == "/onboarding" })
	must(t, expect.Locator(page.Locator(`form[action="/onboarding"]`)).ToHaveCount(0))
	signedInAs(t, page, fakeSSOEmail, fakeSSOEmail)
}
