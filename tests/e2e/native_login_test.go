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
	must(t, expect.Locator(page.Locator(".ts-email-code-slot")).ToHaveCount(6))
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

func TestNativeEmailCodeInputEditing(t *testing.T) {
	t.Parallel()
	base, _ := tsAuthDashboard(t)
	page := rejectingPage(t, base)
	goTo(t, page, "/login/code")
	fill(t, page.Locator("#email"), "code-input@example.test")
	click(t, pageByRole(page, *playwright.AriaRoleButton, "Email me a code"))
	code := page.Locator("#code")
	slots := page.Locator(".ts-email-code-slot")
	must(t, expect.Locator(slots).ToHaveCount(6))
	must(t, expect.Locator(code).ToBeFocused())
	expectAccessible(t, page, "email code entry", "")

	// The native input keeps leading zeroes and normal keyboard editing.
	fill(t, code, "012345")
	must(t, expect.Locator(slots).ToHaveText([]string{"0", "1", "2", "3", "4", "5"}))
	must(t, code.Press("Home"))
	must(t, code.Press("ArrowRight"))
	must(t, code.Press("Delete"))
	must(t, expect.Locator(code).ToHaveValue("02345"))
	must(t, code.Press("End"))
	must(t, code.Press("Backspace"))
	must(t, expect.Locator(code).ToHaveValue("0234"))
	must(t, code.Press("a"))
	must(t, expect.Locator(code).ToHaveValue("0234"))
	must(t, code.Press("Home"))
	must(t, code.Press("ArrowRight"))
	must(t, code.Press("a"))
	must(t, code.Press("9"))
	must(t, expect.Locator(code).ToHaveValue("09234"))

	// Pasting a formatted code replaces the selection and removes separators.
	must(t, code.Press("ControlOrMeta+a"))
	_, err := code.Evaluate(`input => {
		const clipboardData = new DataTransfer();
		clipboardData.setData('text', '908 027');
		input.dispatchEvent(new ClipboardEvent('paste', {clipboardData, bubbles: true, cancelable: true}));
	}`, nil)
	must(t, err)
	must(t, expect.Locator(code).ToHaveValue("908027"))
	must(t, expect.Locator(slots).ToHaveText([]string{"9", "0", "8", "0", "2", "7"}))

	// Each visual box selects its digit for replacement while remaining one
	// tab stop. The input overlays the boxes, so target its relative position.
	box, err := slots.Nth(2).BoundingBox()
	must(t, err)
	inputBox, err := code.BoundingBox()
	must(t, err)
	must(t, code.Click(playwright.LocatorClickOptions{Position: &playwright.Position{
		X: box.X + box.Width/2 - inputBox.X, Y: box.Height / 2,
	}}))
	must(t, code.Press("1"))
	must(t, expect.Locator(code).ToHaveValue("901027"))

	// Dragging in either direction selects a range of visible boxes. Shift-click
	// extends that range, and double-click still selects the complete code.
	position := func(index int) *playwright.Position {
		t.Helper()
		box, err := slots.Nth(index).BoundingBox()
		must(t, err)
		return &playwright.Position{X: box.X + box.Width/2, Y: box.Y + box.Height/2}
	}
	for _, endpoints := range [][2]int{{1, 4}, {4, 1}} {
		fill(t, code, "012345")
		from, to := position(endpoints[0]), position(endpoints[1])
		must(t, page.Mouse().Move(from.X, from.Y))
		must(t, page.Mouse().Down())
		must(t, page.Mouse().Move(to.X, to.Y, playwright.MouseMoveOptions{Steps: new(8)}))
		must(t, page.Mouse().Up())
		must(t, expect.Locator(page.Locator(".ts-email-code-selected")).ToHaveCount(4))
		must(t, code.Press("8"))
		must(t, expect.Locator(code).ToHaveValue("085"))
	}
	fill(t, code, "012345")
	from, to := position(1), position(4)
	must(t, code.Click(playwright.LocatorClickOptions{Position: &playwright.Position{X: from.X - inputBox.X, Y: from.Y - inputBox.Y}}))
	must(t, code.Click(playwright.LocatorClickOptions{
		Position:  &playwright.Position{X: to.X - inputBox.X, Y: to.Y - inputBox.Y},
		Modifiers: []playwright.KeyboardModifier{*playwright.KeyboardModifierShift},
	}))
	must(t, code.Press("8"))
	must(t, expect.Locator(code).ToHaveValue("085"))
	must(t, code.Dblclick())
	must(t, code.Press("9"))
	must(t, expect.Locator(code).ToHaveValue("9"))
	must(t, code.Press("Tab"))
	must(t, expect.Locator(pageByRole(page, *playwright.AriaRoleButton, "Continue")).ToBeFocused())

	for _, width := range []int{320, 390, 1040, 1440} {
		must(t, page.SetViewportSize(width, 844))
		fits, err := page.Evaluate(`document.documentElement.scrollWidth <= innerWidth`)
		must(t, err)
		if fits != true {
			t.Fatalf("code form overflows at width %d", width)
		}
	}
	screenshot(t, page, "email-code-filled")

	fill(t, code, "")
	click(t, pageByRole(page, *playwright.AriaRoleButton, "Continue"))
	must(t, expect.Locator(code).ToHaveAttribute("aria-invalid", "true"))
	must(t, expect.Locator(code).ToHaveAttribute("aria-describedby", "code-error"))
	must(t, expect.Locator(page.Locator("#code-error")).ToContainText("Enter the code"))
	expectAccessible(t, page, "email code error", "")
	screenshot(t, page, "email-code-error")
}

func TestNativeEmailCodeFormattedDrop(t *testing.T) {
	t.Parallel()
	base, _ := tsAuthDashboard(t)
	page := rejectingPage(t, base)
	goTo(t, page, "/login/code")
	fill(t, page.Locator("#email"), "drop-code@example.test")
	click(t, pageByRole(page, *playwright.AriaRoleButton, "Email me a code"))
	code := page.Locator("#code")
	visible(t, page.Locator(".ts-email-code-slot").First())
	for _, test := range []struct{ name, text, want string }{
		{"spaces", "123 456", "123456"},
		{"leading zeros", "012-345", "012345"},
		{"long code", "123 456 789", "123456"},
	} {
		t.Run(test.name, func(t *testing.T) {
			fill(t, code, "")
			_, err := page.Evaluate(`text => {
				document.querySelector('#code-drag-source')?.remove();
				const source = document.createElement('div');
				source.id = 'code-drag-source';
				source.draggable = true;
				source.textContent = text;
				source.style.cssText = 'position:fixed;top:8px;left:8px;padding:8px;z-index:9999';
				source.addEventListener('dragstart', event => event.dataTransfer.setData('text/plain', text));
				document.body.append(source);
			}`, test.text)
			must(t, err)
			// Use a real native drop so the browser applies maxlength before input.
			must(t, page.Locator("#code-drag-source").DragTo(code))
			must(t, expect.Locator(code).ToHaveValue(test.want))
			must(t, expect.Locator(page.Locator(".ts-email-code-slot")).ToHaveText(strings.Split(test.want, "")))
		})
	}
}

func TestNativeEmailCodePasteUndo(t *testing.T) {
	t.Parallel()
	base, _ := tsAuthDashboard(t)
	page := rejectingPage(t, base)
	must(t, page.Context().GrantPermissions([]string{"clipboard-read", "clipboard-write"}))
	for _, test := range []struct {
		name, clipboard, want string
		start, end            int
	}{
		{"digits", "908027", "908027", 0, 6},
		{"formatted", "908 027", "908027", 0, 6},
		{"selected range", "9 0", "09045", 1, 4},
		{"long digits", "908027123", "908027", 0, 6},
		{"long selected range", "98765", "098745", 1, 4},
	} {
		t.Run(test.name, func(t *testing.T) {
			goTo(t, page, "/login/code")
			fill(t, page.Locator("#email"), "paste-undo@example.test")
			click(t, pageByRole(page, *playwright.AriaRoleButton, "Email me a code"))
			code := page.Locator("#code")
			slots := page.Locator(".ts-email-code-slot")
			fill(t, code, "012345")
			_, err := code.Evaluate(`(input, range) => input.setSelectionRange(range[0], range[1])`, []int{test.start, test.end})
			must(t, err)
			_, err = page.Evaluate(`text => navigator.clipboard.writeText(text)`, test.clipboard)
			must(t, err)
			// Exercise the real browser paste transaction, then undo and redo it.
			must(t, code.Press("ControlOrMeta+v"))
			must(t, expect.Locator(code).ToHaveValue(test.want))
			must(t, code.Press("ControlOrMeta+z"))
			must(t, expect.Locator(code).ToHaveValue("012345"))
			must(t, expect.Locator(slots).ToHaveText([]string{"0", "1", "2", "3", "4", "5"}))
			must(t, code.Press("ControlOrMeta+Shift+z"))
			must(t, expect.Locator(code).ToHaveValue(test.want))
			wantSlots := make([]string, 6)
			copy(wantSlots, strings.Split(test.want, ""))
			must(t, expect.Locator(slots).ToHaveText(wantSlots))
		})
	}
}

func TestNativeEmailCodeBeforeDatastarLoads(t *testing.T) {
	t.Parallel()
	base, _ := tsAuthDashboard(t)
	for _, test := range []struct {
		name, text string
		end        int
	}{
		{"digits", "012345", 4},
		{"formatted", "012 345", 5},
	} {
		t.Run(test.name, func(t *testing.T) {
			page := watchedPage(t, newContext(t, base))
			goTo(t, page, "/login/code")
			fill(t, page.Locator("#email"), "early-code@example.test")

			// Hold the real module response so the fallback field can receive input
			// before binding, without replacing the script or bypassing its integrity.
			pending := make(chan playwright.Route, 1)
			must(t, page.Route("**/js/datastar.js*", func(route playwright.Route) { pending <- route }))
			_, err := pageByRole(page, *playwright.AriaRoleButton, "Email me a code").Evaluate(`button => button.click()`, nil)
			must(t, err)
			code := page.Locator("#code")
			must(t, expect.Locator(code).ToBeVisible())
			must(t, expect.Locator(page.Locator(".ts-email-code-ready")).ToHaveCount(0))
			must(t, code.PressSequentially(test.text))
			_, err = code.Evaluate(`(input, end) => input.setSelectionRange(1, end)`, test.end)
			must(t, err)
			select {
			case route := <-pending:
				must(t, route.Continue())
			case <-time.After(10 * time.Second):
				t.Fatal("Datastar module request was not intercepted")
			}
			must(t, expect.Locator(page.Locator(".ts-email-code-slot")).ToHaveText([]string{"0", "1", "2", "3", "4", "5"}))
			must(t, expect.Locator(code).ToHaveValue("012345"))
			selection, err := code.Evaluate(`input => input.selectionStart === 1 && input.selectionEnd === 4`, nil)
			must(t, err)
			if selection != true {
				t.Fatal("Datastar initialization changed the selected digits")
			}
			must(t, expect.Locator(page.Locator(".ts-email-code-selected")).ToHaveCount(3))
		})
	}
}

func TestNativeSignUpWithoutJavaScript(t *testing.T) {
	t.Parallel()
	base, fake := tsAuthDashboard(t)
	context, err := browser.NewContext(playwright.BrowserNewContextOptions{
		BaseURL: new(base), JavaScriptEnabled: new(false),
	})
	must(t, err)
	t.Cleanup(func() { _ = context.Close() })
	page := watchedPage(t, context)
	email := strings.ToLower(uniqueName("nojs")) + "@example.test"
	goTo(t, page, "/onboarding")
	fill(t, page.Locator("#displayName"), "Native Form")
	fill(t, page.Locator("#email"), email)
	fill(t, page.Locator("#password"), "native form password 1")
	fill(t, page.Locator("#confirmPassword"), "native form password 1")
	must(t, page.Locator("#terms").Check())
	click(t, pageByRole(page, *playwright.AriaRoleButton, "Create account"))
	must(t, expect.Locator(page.Locator("h1")).ToHaveText("Verify your email"))
	code := fake.code(t, email)
	// Native typing must preserve the separator until the server normalizes it.
	must(t, page.Locator("#code").PressSequentially(code[:3]+" "+code[3:]))
	click(t, pageByRole(page, *playwright.AriaRoleButton, "Continue"))
	waitForPath(t, page, func(u *url.URL) bool { return u.Path == "/onboarding" })
	signedInAs(t, page, email, "Native Form")
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

func TestNativeLoginStaysNativeWithoutAdvertisedHeadlessGrants(t *testing.T) {
	t.Parallel()
	base, fake := tsAuthDashboard(t)
	fake.mu.Lock()
	fake.legacy = true
	fake.mu.Unlock()
	page := watchedPage(t, newContext(t, base))
	for _, path := range []string{"/login", "/login/code", "/onboarding", "/reset-password"} {
		goTo(t, page, path)
		must(t, expect.Locator(page.Locator(`form[action="`+path+`"]`)).ToBeVisible())
	}
	email := strings.ToLower(uniqueName("legacy")) + "@example.test"
	signUpThroughTSAuth(t, page, fake, "Native Forms", email, "native password 1")
	signedInAs(t, page, email, "Native Forms")
	logout(t, page)
	login(t, page, email, "native password 1")
	signedInAs(t, page, email, "Native Forms")
}
