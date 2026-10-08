package auth

import (
	"context"
	"crypto/rand"
	"crypto/rsa"
	"encoding/json/v2"
	"errors"
	"io"
	"log/slog"
	"net/http"
	"net/http/httptest"
	"net/netip"
	"net/url"
	"regexp"
	"strings"
	"sync/atomic"
	"testing"
	"time"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/core/memstore"
)

// fakeHeadless is ts-auth's headless API as TS-582 specifies it: password
// and email-code grants at the token endpoint, JSON steps that only send
// mail, and revocation. It checks client authentication and the end user's
// address, and never says whether an address has an account.
type fakeHeadless struct {
	users map[string]*fakeUser
	// codes are the live emailed sign-in codes by address; signUps the
	// addresses waiting for theirs to open an account; resets the live reset
	// codes.
	codes, resets map[string]string
	signUps       map[string]fakeSignUp
	calls         []fakeCall
	revoked       []string
	// status, when set, answers every call with it.
	status      int
	retryAfter  string
	location    string
	omitIDToken bool
}

// fakeSignUp is a sign-up waiting for its code, which only its requester's
// pending token redeems.
type fakeSignUp struct{ password, token string }

type fakeUser struct {
	subject, password string
}

type fakeCall struct {
	path   string
	fields map[string]string
}

func (i *testIssuer) mountHeadless(mux *http.ServeMux) {
	i.headless = &fakeHeadless{users: map[string]*fakeUser{}, codes: map[string]string{}, resets: map[string]string{}, signUps: map[string]fakeSignUp{}}
	for _, path := range []string{headlessCodeStart, headlessSignUp, headlessResetStart, headlessResetComplete} {
		mux.HandleFunc("POST "+path, i.serveHeadless)
	}
	mux.HandleFunc("POST /oauth/revoke", func(w http.ResponseWriter, r *http.Request) {
		if clientID, secret, ok := r.BasicAuth(); !ok || clientID != testClientID || secret != testClientSecret {
			writeJSON(w, http.StatusUnauthorized, map[string]string{"error": "invalid_client"})
			return
		}
		i.mu.Lock()
		defer i.mu.Unlock()
		i.headless.revoked = append(i.headless.revoked, r.PostFormValue("token"))
		delete(i.refreshes, r.PostFormValue("token"))
	})
}

// refuse answers with the JSON API's error envelope.
func refuse(w http.ResponseWriter, status int, code string) {
	writeJSON(w, status, map[string]any{"error": map[string]string{"code": code, "message": "That password is too common."}})
}

// limited answers every call while h.status is set; i.mu is held.
func (h *fakeHeadless) limited(w http.ResponseWriter, path string, fields map[string]string) bool {
	h.calls = append(h.calls, fakeCall{path: path, fields: fields})
	if h.status == 0 {
		return false
	}
	if h.retryAfter != "" {
		w.Header().Set("Retry-After", h.retryAfter)
	}
	if h.location != "" {
		w.Header().Set("Location", h.location)
	}
	writeJSON(w, h.status, map[string]string{"error": "rate_limited"})
	return true
}

func (i *testIssuer) serveHeadless(w http.ResponseWriter, r *http.Request) {
	if clientID, secret, ok := r.BasicAuth(); !ok || clientID != testClientID || secret != testClientSecret {
		refuse(w, http.StatusUnauthorized, "invalid_client")
		return
	}
	var fields map[string]string
	body, _ := io.ReadAll(r.Body)
	if r.Header.Get("Content-Type") != "application/json" || json.Unmarshal(body, &fields) != nil {
		refuse(w, http.StatusBadRequest, "invalid_request")
		return
	}
	if _, err := netip.ParseAddr(fields["ip_address"]); err != nil {
		refuse(w, http.StatusBadRequest, "invalid_request")
		return
	}
	i.mu.Lock()
	defer i.mu.Unlock()
	h := i.headless
	if h.limited(w, r.URL.Path, fields) {
		return
	}
	email := fields["email"]
	user := h.users[email]
	switch r.URL.Path {
	case headlessCodeStart:
		if user != nil {
			h.codes[email] = "123456"
		}
	case headlessSignUp:
		if len(fields["password"]) < 8 {
			refuse(w, http.StatusBadRequest, "invalid_password")
			return
		}
		token := rand.Text()
		if user != nil {
			h.codes[email] = "123456"
		} else {
			h.signUps[email] = fakeSignUp{password: fields["password"], token: token}
			h.codes[email] = "654321"
		}
		writeJSON(w, http.StatusAccepted, map[string]string{"pending_token": token})
		return
	case headlessResetStart:
		if user != nil {
			h.resets[email] = "777777"
		}
	case headlessResetComplete:
		if code, found := h.resets[email]; !found || code != fields["code"] {
			refuse(w, http.StatusBadRequest, "invalid_code")
			return
		}
		if strings.Contains(fields["new_password"], "password") {
			refuse(w, http.StatusBadRequest, "invalid_password")
			return
		}
		delete(h.resets, email)
		user.password = fields["new_password"]
		w.WriteHeader(http.StatusNoContent)
		return
	}
	w.WriteHeader(http.StatusAccepted)
}

// headlessGrant answers the password and email-code grants; i.mu is held.
func (i *testIssuer) headlessGrant(w http.ResponseWriter, r *http.Request) {
	fields := map[string]string{}
	for name := range r.PostForm {
		fields[name] = r.PostForm.Get(name)
	}
	h := i.headless
	if h.limited(w, fields["grant_type"], fields) {
		return
	}
	if _, err := netip.ParseAddr(fields["ip_address"]); err != nil || !strings.Contains(fields["scope"], "offline_access") {
		writeJSON(w, http.StatusBadRequest, map[string]string{"error": "invalid_request"})
		return
	}
	email := fields["email"]
	user := h.users[email]
	switch fields["grant_type"] {
	case passwordGrant:
		if user == nil || user.password == "" || user.password != fields["password"] {
			writeJSON(w, http.StatusBadRequest, map[string]string{"error": "invalid_grant"})
			return
		}
	case emailCodeGrant:
		if code, found := h.codes[email]; !found || code != fields["code"] {
			writeJSON(w, http.StatusBadRequest, map[string]string{"error": "invalid_grant"})
			return
		}
		if signUp, pending := h.signUps[email]; pending && user == nil {
			if fields["pending_token"] != signUp.token {
				writeJSON(w, http.StatusBadRequest, map[string]string{"error": "invalid_grant"})
				return
			}
			delete(h.signUps, email)
			user = &fakeUser{subject: "user_" + rand.Text(), password: signUp.password}
			h.users[email] = user
		}
		if user == nil {
			writeJSON(w, http.StatusBadRequest, map[string]string{"error": "invalid_grant"})
			return
		}
		delete(h.codes, email)
	}
	now := time.Now()
	// ts-auth keeps no names: its ID tokens carry none.
	claims := map[string]any{
		"iss": i.url(), "aud": testClientID, "sub": user.subject, "iat": now.Unix(), "exp": now.Add(time.Hour).Unix(),
		"email": email, "email_verified": true,
	}
	i.claims(claims)
	refresh := rand.Text()
	i.refreshes[refresh] = user.subject
	answer := map[string]any{
		"access_token": rand.Text(), "token_type": "Bearer", "expires_in": 3600,
		"refresh_token": refresh, "id_token": i.sign(claims),
	}
	if h.omitIDToken {
		delete(answer, "id_token")
	}
	writeJSON(w, http.StatusOK, answer)
}

func (i *testIssuer) addUser(email, password string) {
	i.set(func(i *testIssuer) {
		i.headless.users[email] = &fakeUser{subject: "user_" + rand.Text(), password: password}
	})
}

// headlessCalls returns how many times the dashboard called path, or used
// the grant it names.
func (i *testIssuer) headlessCalls(path string) int {
	i.mu.Lock()
	defer i.mu.Unlock()
	count := 0
	for _, call := range i.headless.calls {
		if call.path == path {
			count++
		}
	}
	return count
}

// post sends a form with the browser's cookies from a fixed client.
func (d *tsAuthDashboard) post(target string, form url.Values) *httptest.ResponseRecorder {
	request := httptest.NewRequest(http.MethodPost, "http://dashboard.test"+target, strings.NewReader(form.Encode()))
	request.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	request.RemoteAddr = "[2001:db8::7]:4321"
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

func TestNativePasswordSignIn(t *testing.T) {
	t.Parallel()
	issuer := newTestIssuer(t)
	issuer.addUser("grace@example.test", "correct horse battery")
	dashboard := newTSAuthDashboard(t, issuer)
	response := dashboard.post("/login", url.Values{"email": {" Grace@Example.test "}, "password": {"correct horse battery"}, "redirectTo": {"/databases"}})
	if response.Code != http.StatusSeeOther || response.Header().Get("Location") != "/databases" || !dashboard.signedIn(t) {
		t.Fatalf("password sign-in = %d → %q, want a session and the return path", response.Code, response.Header().Get("Location"))
	}
	account, err := dashboard.store.FindByEmail(t.Context(), "grace@example.test")
	if err != nil || !strings.HasPrefix(account.User.AuthSubject, "user_") {
		t.Fatalf("account = %+v, %v; want one linked to the ts-auth user", account.User, err)
	}
	call := issuer.headless.calls[0]
	if call.fields["email"] != "grace@example.test" || call.fields["ip_address"] != "2001:db8::7" {
		t.Fatalf("ts-auth saw %v, want the normalized email and the browser's own address", call.fields)
	}
	// The session is rechecked with the refresh token ts-auth returned.
	dashboard.expire(t)
	if !dashboard.signedIn(t) || issuer.refreshCount() != 1 {
		t.Fatalf("refresh after native sign-in: signed in=%v refreshes=%d", dashboard.signedIn(t), issuer.refreshCount())
	}
	// Signing out ends the ts-auth sign-in too.
	record, _ := dashboard.record(t)
	var state refreshState
	if err := json.Unmarshal([]byte(record.Refresh), &state); err != nil {
		t.Fatal(err)
	}
	dashboard.do(http.MethodPost, "/api/v1/auth/logout")
	if issuer.headless.revoked == nil || issuer.headless.revoked[0] != state.Token || dashboard.signedIn(t) {
		t.Fatalf("logout revoked %v, want the session's refresh token", issuer.headless.revoked)
	}
}

func TestNativePasswordSignInDoesNotRevealAccounts(t *testing.T) {
	t.Parallel()
	issuer := newTestIssuer(t)
	issuer.addUser("grace@example.test", "correct horse battery")
	issuer.set(func(i *testIssuer) { i.headless.users["codes-only@example.test"] = &fakeUser{subject: "user_codes"} })
	dashboard := newTSAuthDashboard(t, issuer)
	for _, email := range []string{"grace@example.test", "nobody@example.test", "codes-only@example.test"} {
		response := dashboard.post("/login", url.Values{"email": {email}, "password": {"wrong password!"}})
		if response.Code != http.StatusUnauthorized || !strings.Contains(response.Body.String(), "Incorrect email or password.") {
			t.Fatalf("refused sign-in for %s = %d, want 401 with the shared message", email, response.Code)
		}
	}
	if dashboard.signedIn(t) {
		t.Fatal("refused sign-in started a session")
	}
}

func TestNativePasswordSignInIsThrottled(t *testing.T) {
	t.Parallel()
	issuer := newTestIssuer(t)
	dashboard := newTSAuthDashboard(t, issuer)
	var response *httptest.ResponseRecorder
	for range 11 {
		response = dashboard.post("/login", url.Values{"email": {"grace@example.test"}, "password": {"guess guess guess"}})
	}
	if response.Code != http.StatusTooManyRequests || response.Header().Get("Retry-After") == "" {
		t.Fatalf("11th guess = %d, want 429 with Retry-After", response.Code)
	}
	if calls := issuer.headlessCalls(passwordGrant); calls != 10 {
		t.Fatalf("ts-auth checked %d guesses, want the dashboard to stop at its budget of 10", calls)
	}

	// Both SDK error shapes retain the issuer's rate limit and bounded wait.
	limited := newTestIssuer(t)
	for _, path := range []string{"/login", "/login/code"} {
		for _, retry := range []struct{ header, want string }{
			{"42", "42"}, {"99999999999", "3600"}, {"9223372036854775808", "3600"}, {"18446744073709551616", "3600"},
			{"", "1"}, {"0", "1"}, {"-5", "1"}, {"invalid", "1"},
		} {
			limited.set(func(i *testIssuer) {
				i.headless.status, i.headless.retryAfter = http.StatusTooManyRequests, retry.header
			})
			response = newTSAuthDashboard(t, limited).post(path, url.Values{"email": {"grace@example.test"}, "password": {"guess guess guess"}})
			if response.Code != http.StatusTooManyRequests || response.Header().Get("Retry-After") != retry.want {
				t.Fatalf("POST %s with issuer Retry-After %q = %d Retry-After %q, want 429 and %s", path, retry.header, response.Code, response.Header().Get("Retry-After"), retry.want)
			}
		}
	}
}

func TestNativeSignInRejectsUnprovenTokens(t *testing.T) {
	t.Parallel()
	forger, err := rsa.GenerateKey(rand.Reader, 2048)
	if err != nil {
		t.Fatal(err)
	}
	for name, change := range map[string]func(*testIssuer){
		"forged signature": func(i *testIssuer) { i.signer = forger },
		"other audience":   func(i *testIssuer) { i.claims = func(c map[string]any) { c["aud"] = "other-client" } },
		"unverified email": func(i *testIssuer) { i.claims = func(c map[string]any) { c["email_verified"] = false } },
		"other issuer":     func(i *testIssuer) { i.claims = func(c map[string]any) { c["iss"] = "https://evil.example" } },
		"empty subject":    func(i *testIssuer) { i.claims = func(c map[string]any) { c["sub"] = "" } },
		"expired token": func(i *testIssuer) {
			i.claims = func(c map[string]any) { c["exp"] = time.Now().Add(-time.Hour).Unix() }
		},
		"missing ID token": func(i *testIssuer) { i.headless.omitIDToken = true },
	} {
		t.Run(name, func(t *testing.T) {
			t.Parallel()
			issuer := newTestIssuer(t)
			issuer.addUser("grace@example.test", "correct horse battery")
			issuer.set(change)
			dashboard := newTSAuthDashboard(t, issuer)
			response := dashboard.post("/login", url.Values{"email": {"grace@example.test"}, "password": {"correct horse battery"}})
			if response.Code < http.StatusBadRequest || dashboard.signedIn(t) {
				t.Fatalf("sign-in = %d, want a refusal and no session", response.Code)
			}
		})
	}
}

func TestNativeHeadlessDoesNotFollowRedirects(t *testing.T) {
	t.Parallel()
	var redirected atomic.Int32
	target := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		redirected.Add(1)
		w.WriteHeader(http.StatusOK)
	}))
	t.Cleanup(target.Close)
	issuer := newTestIssuer(t)
	issuer.set(func(i *testIssuer) { i.headless.status, i.headless.location = http.StatusTemporaryRedirect, target.URL })
	for _, path := range []string{"/login", "/login/code", "/reset-password"} {
		dashboard := newTSAuthDashboard(t, issuer)
		response := dashboard.post(path, url.Values{"email": {"grace@example.test"}, "password": {"correct horse battery"}})
		if response.Code != http.StatusServiceUnavailable || response.Header().Get("Location") != "" || dashboard.signedIn(t) {
			t.Fatalf("POST %s with redirected issuer = %d %q, want 503 and no session", path, response.Code, response.Header().Get("Location"))
		}
	}
	if redirected.Load() != 0 {
		t.Fatalf("forwarded %d credential-bearing requests to a redirect target", redirected.Load())
	}
}

func TestNativeSignInWhileTSAuthFails(t *testing.T) {
	t.Parallel()
	for _, status := range []int{http.StatusBadGateway, http.StatusUnauthorized} {
		issuer := newTestIssuer(t)
		dashboard := newTSAuthDashboard(t, issuer)
		issuer.set(func(i *testIssuer) { i.headless.status = status })
		response := dashboard.post("/login", url.Values{"email": {"grace@example.test"}, "password": {"correct horse battery"}})
		if response.Code != http.StatusServiceUnavailable || !strings.Contains(response.Body.String(), unavailable) || dashboard.signedIn(t) {
			t.Fatalf("sign-in with ts-auth answering %d = %d, want 503 and no session", status, response.Code)
		}
	}
}

func TestNativeEmailCodeSignIn(t *testing.T) {
	t.Parallel()
	issuer := newTestIssuer(t)
	issuer.addUser("grace@example.test", "correct horse battery")
	dashboard := newTSAuthDashboard(t, issuer)

	// Known and unknown addresses get the same code step.
	for _, email := range []string{"grace@example.test", "nobody@example.test"} {
		response := dashboard.post("/login/code", url.Values{"email": {email}})
		if response.Code != http.StatusOK || !strings.Contains(response.Body.String(), "If "+email+" has a TerraScale account, we emailed it a code.") {
			t.Fatalf("code request for %s = %d, want the code step", email, response.Code)
		}
	}
	if response := dashboard.post("/login/code", url.Values{"email": {"not-an-email"}}); response.Code != http.StatusUnprocessableEntity {
		t.Fatalf("code request for an invalid address = %d, want 422", response.Code)
	}
	if response := dashboard.post("/login/code/verify", url.Values{"email": {"grace@example.test"}, "code": {"000000"}}); response.Code != http.StatusUnauthorized || dashboard.signedIn(t) {
		t.Fatalf("wrong code = %d, want 401 and no session", response.Code)
	}
	response := dashboard.post("/login/code/verify", url.Values{"email": {"grace@example.test"}, "code": {"123 456"}, "redirectTo": {"https://evil.example/"}})
	if response.Code != http.StatusSeeOther || response.Header().Get("Location") != "/onboarding" || !dashboard.signedIn(t) {
		t.Fatalf("right code = %d → %q, want a session on the dashboard", response.Code, response.Header().Get("Location"))
	}
}

func TestNativeCodeRequestsAreThrottled(t *testing.T) {
	t.Parallel()
	issuer := newTestIssuer(t)
	dashboard := newTSAuthDashboard(t, issuer)
	var response *httptest.ResponseRecorder
	for range 11 {
		response = dashboard.post("/login/code", url.Values{"email": {"grace@example.test"}})
	}
	if response.Code != http.StatusTooManyRequests || issuer.headlessCalls(headlessCodeStart) != 10 {
		t.Fatalf("11th code request = %d after %d mails, want 429 after 10", response.Code, issuer.headlessCalls(headlessCodeStart))
	}
}

func TestNativeSignUpVerifiesEmail(t *testing.T) {
	t.Parallel()
	issuer := newTestIssuer(t)
	issuer.addUser("grace@example.test", "correct horse battery")
	dashboard := newTSAuthDashboard(t, issuer)
	tsAuth, deps := dashboard.tsAuth, dashboard.deps
	dashboard.mux.HandleFunc("POST /sign-up", func(w http.ResponseWriter, r *http.Request) {
		_ = r.ParseForm()
		err := tsAuth.SignUp(w, r, deps, RegistrationInput{
			DisplayName: r.PostForm.Get("name"), Email: r.PostForm.Get("email"), Password: r.PostForm.Get("password"),
			ConfirmPassword: r.PostForm.Get("password"), AcceptedTerms: true,
		}, "/onboarding?plan=starter")
		if err != nil {
			http.Error(w, err.Error(), http.StatusUnprocessableEntity)
		}
	})
	form := url.Values{"name": {"Ada Lovelace"}, "email": {"ada@example.test"}, "password": {"analytical engine"}}
	response := dashboard.post("/sign-up", form)
	if response.Code != http.StatusOK || !strings.Contains(response.Body.String(), "Verify your email") || dashboard.signedIn(t) {
		t.Fatalf("sign-up = %d, want the verification step and no session yet", response.Code)
	}
	// A taken address reads the same.
	form.Set("email", "grace@example.test")
	elsewhere := *dashboard
	elsewhere.cookies = map[string]*http.Cookie{}
	if taken := elsewhere.post("/sign-up", form); taken.Code != http.StatusOK || !strings.Contains(taken.Body.String(), "Verify your email") {
		t.Fatalf("sign-up with a taken address = %d, want the same verification step", taken.Code)
	}
	if short := dashboard.post("/sign-up", url.Values{"name": {"Ada"}, "email": {"ada@example.test"}, "password": {"short"}}); short.Code != http.StatusUnprocessableEntity {
		t.Fatalf("sign-up with a short password = %d, want 422 before ts-auth", short.Code)
	}
	if calls := issuer.headlessCalls(headlessSignUp); calls != 2 {
		t.Fatalf("ts-auth saw %d sign-ups, want 2", calls)
	}
	// Someone else holding the mailed code, but not this browser's pending
	// token, cannot open the account.
	stranger := *dashboard
	stranger.cookies = map[string]*http.Cookie{}
	if response := stranger.post("/login/code/verify", url.Values{"email": {"ada@example.test"}, "code": {"654321"}, "signUp": {"1"}}); response.Code != http.StatusUnauthorized {
		t.Fatalf("code without the pending token = %d, want 401", response.Code)
	}
	if _, ok := dashboard.cookies["ts_dashboard_signup"]; !ok {
		t.Fatal("sign-up kept no pending token")
	}
	response = dashboard.post("/login/code/verify", url.Values{
		"email": {"ada@example.test"}, "code": {"654321"}, "signUp": {"1"}, "name": {"Ada Lovelace"}, "redirectTo": {"/onboarding?plan=starter"},
	})
	if response.Code != http.StatusSeeOther || response.Header().Get("Location") != "/onboarding?plan=starter" || !dashboard.signedIn(t) {
		t.Fatalf("verification = %d → %q, want a session continuing sign-up", response.Code, response.Header().Get("Location"))
	}
	if _, ok := dashboard.cookies["ts_dashboard_signup"]; ok {
		t.Fatal("the pending token outlived the sign-up")
	}
	if account, err := dashboard.store.FindByEmail(t.Context(), "ada@example.test"); err != nil || account.User.Name != "Ada Lovelace" {
		t.Fatalf("new account = %+v, %v; want the name given at sign-up", account.User, err)
	}
	// The password given at sign-up works once the address is verified.
	dashboard.do(http.MethodPost, "/api/v1/auth/logout")
	if response := dashboard.post("/login", url.Values{"email": {"ada@example.test"}, "password": {"analytical engine"}}); response.Code != http.StatusSeeOther {
		t.Fatalf("sign-in with the sign-up password = %d", response.Code)
	}
}

func TestNativePasswordReset(t *testing.T) {
	t.Parallel()
	issuer := newTestIssuer(t)
	issuer.addUser("grace@example.test", "correct horse battery")
	dashboard := newTSAuthDashboard(t, issuer)
	// Another browser, say an attacker's, is signed in with the old password.
	other := *dashboard
	other.cookies = map[string]*http.Cookie{}
	if response := other.post("/login", url.Values{"email": {"grace@example.test"}, "password": {"correct horse battery"}}); response.Code != http.StatusSeeOther {
		t.Fatalf("sign-in with the old password = %d", response.Code)
	}
	for _, email := range []string{"grace@example.test", "nobody@example.test"} {
		if response := dashboard.post("/reset-password", url.Values{"email": {email}}); response.Code != http.StatusOK || !strings.Contains(response.Body.String(), "Choose a new password") {
			t.Fatalf("reset request for %s = %d, want the code step", email, response.Code)
		}
	}
	reset := func(code, password, confirm string) *httptest.ResponseRecorder {
		return dashboard.post("/reset-password/complete", url.Values{"email": {"grace@example.test"}, "code": {code}, "password": {password}, "confirmPassword": {confirm}})
	}
	if response := reset("777777", "new password 123", "new password 124"); response.Code != http.StatusUnprocessableEntity || issuer.headlessCalls(headlessResetComplete) != 0 {
		t.Fatalf("mismatched confirmation = %d, want 422 before ts-auth", response.Code)
	}
	if response := reset("000000", "a fresh secret 1", "a fresh secret 1"); response.Code != http.StatusUnauthorized {
		t.Fatalf("wrong code = %d, want 401", response.Code)
	}
	if response := reset("777777", "new password 123", "new password 123"); response.Code != http.StatusUnprocessableEntity || !strings.Contains(response.Body.String(), "That password is too common.") {
		t.Fatalf("refused password = %d, want ts-auth's reason", response.Code)
	}
	response := reset("777777", "a fresh secret 1", "a fresh secret 1")
	location, _ := url.Parse(response.Header().Get("Location"))
	if response.Code != http.StatusSeeOther || location.Path != "/login" || location.Query().Get("notice") != passwordResetNotice {
		t.Fatalf("reset = %d → %q, want sign-in with a notice", response.Code, location)
	}
	if notice := dashboard.do(http.MethodGet, location.String()); !strings.Contains(notice.Body.String(), "Your password was changed.") {
		t.Fatal("sign-in page does not confirm the reset")
	}
	if other.signedIn(t) {
		t.Fatal("a session from before the reset is still signed in")
	}
	if response := dashboard.post("/login", url.Values{"email": {"grace@example.test"}, "password": {"a fresh secret 1"}}); response.Code != http.StatusSeeOther {
		t.Fatalf("sign-in with the new password = %d", response.Code)
	}
}

func TestNativeLoginNoticeIsNamedNotText(t *testing.T) {
	t.Parallel()
	dashboard := newTSAuthDashboard(t, newTestIssuer(t))
	response := dashboard.do(http.MethodGet, "/login?notice="+url.QueryEscape("Call +1 555 0100 to verify your account"))
	if strings.Contains(response.Body.String(), "555 0100") {
		t.Fatal("login page shows a notice a link wrote")
	}
}

// TestSignInFailureOffersRecovery covers a failed single sign-on: the page's
// way back must open the dashboard's sign-in page rather than ts-auth, which
// would return the still signed-in browser to the same failure, and its
// reference must find the log line. Not parallel: it captures slog.
func TestSignInFailureOffersRecovery(t *testing.T) {
	var logs strings.Builder
	previous := slog.Default()
	slog.SetDefault(slog.New(slog.NewTextHandler(&logs, nil)))
	t.Cleanup(func() { slog.SetDefault(previous) })

	issuer := newTestIssuer(t)
	issuer.set(func(i *testIssuer) { i.claims = func(c map[string]any) { c["email_verified"] = false } })
	dashboard := newTSAuthDashboard(t, issuer)
	response := dashboard.signIn(t, issuer, "/login/sso?returnTo=%2Fdatabases")
	body := response.Body.String()
	if response.Code != http.StatusForbidden || !strings.Contains(body, `href="/login?prompt=login&amp;returnTo=%2Fdatabases">Try again`) ||
		!strings.Contains(body, `href="/login/code?returnTo=%2Fdatabases"`) {
		t.Fatalf("failed sign-in = %d, want recovery links: %s", response.Code, body)
	}
	ref := regexp.MustCompile(`Reference: <code>([a-z0-9]{10})</code>`).FindStringSubmatch(body)
	if ref == nil || !strings.Contains(logs.String(), "ref="+ref[1]) || !strings.Contains(logs.String(), "unverified") {
		t.Fatalf("page reference %v not in the log line:\n%s", ref, logs.String())
	}
	if again := dashboard.do(http.MethodGet, "/login"); again.Code != http.StatusOK || !strings.Contains(again.Body.String(), `action="/login"`) {
		t.Fatalf("Try again = %d, want the dashboard's sign-in page", again.Code)
	}
}

func TestNativePagesKeepTheContinuation(t *testing.T) {
	t.Parallel()
	dashboard := newTSAuthDashboard(t, newTestIssuer(t))
	// A refused password keeps single sign-on going to the same page.
	refused := dashboard.post("/login", url.Values{"email": {"grace@example.test"}, "password": {"wrong password!"}, "redirectTo": {"/databases"}})
	if !strings.Contains(refused.Body.String(), `href="/login/sso?returnTo=%2Fdatabases"`) {
		t.Fatalf("refused sign-in page lost the single sign-on continuation: %s", refused.Body)
	}
	// After signing out without ts-auth's end-session endpoint, single
	// sign-on keeps asking for credentials however many passwords fail.
	if form := dashboard.do(http.MethodGet, "/login?prompt=login"); !strings.Contains(form.Body.String(), `name="prompt" value="login"`) {
		t.Fatal("sign-in form does not carry prompt=login")
	}
	refused = dashboard.post("/login", url.Values{"email": {"grace@example.test"}, "password": {""}, "prompt": {"login"}})
	if !strings.Contains(refused.Body.String(), `href="/login/sso?prompt=login&amp;returnTo=%2F"`) {
		t.Fatalf("refused sign-in page dropped prompt=login: %s", refused.Body)
	}
	if code := dashboard.do(http.MethodGet, "/login/code?returnTo=%2Fdatabases"); !strings.Contains(code.Body.String(), `href="/login?returnTo=%2Fdatabases">← Back to sign in`) {
		t.Fatalf("code page's way back lost the continuation: %s", code.Body)
	}
}

func TestNativeBearerLogoutRevokesTSAuthSignIn(t *testing.T) {
	t.Parallel()
	issuer := newTestIssuer(t)
	issuer.addUser("grace@example.test", "correct horse battery")
	dashboard := newTSAuthDashboard(t, issuer)
	dashboard.post("/login", url.Values{"email": {"grace@example.test"}, "password": {"correct horse battery"}})
	record, _ := dashboard.record(t)
	var state refreshState
	if err := json.Unmarshal([]byte(record.Refresh), &state); err != nil {
		t.Fatal(err)
	}
	request := httptest.NewRequest(http.MethodPost, "http://dashboard.test/api/v1/auth/logout", nil)
	request.Header.Set("Authorization", "Bearer "+dashboard.cookies["ts_dashboard_session"].Value)
	dashboard.mux.ServeHTTP(httptest.NewRecorder(), request)
	if issuer.headless.revoked == nil || issuer.headless.revoked[0] != state.Token || dashboard.signedIn(t) {
		t.Fatalf("bearer logout revoked %v, want the session's refresh token", issuer.headless.revoked)
	}
}

func TestNativePagesStayNativeWithoutHeadlessDiscovery(t *testing.T) {
	t.Parallel()
	issuer := newTestIssuer(t)
	issuer.set(func(i *testIssuer) { i.legacy = true })
	issuer.addUser("grace@example.test", "correct horse battery")
	dashboard := newTSAuthDashboard(t, issuer)
	for _, path := range []string{"/login?returnTo=%2Fdatabases", "/login/code?returnTo=%2Fdatabases", "/reset-password"} {
		response := dashboard.do(http.MethodGet, path)
		if response.Code != http.StatusOK || response.Header().Get("Location") != "" {
			t.Fatalf("GET %s = %d %q, want a native page", path, response.Code, response.Header().Get("Location"))
		}
	}
	if response := dashboard.post("/login/code", url.Values{"email": {"grace@example.test"}}); response.Code != http.StatusOK {
		t.Fatalf("code request without advertised grants = %d, want the native code step", response.Code)
	}
	if response := dashboard.post("/login", url.Values{"email": {"grace@example.test"}, "password": {"correct horse battery"}, "redirectTo": {"/databases"}}); response.Code != http.StatusSeeOther || response.Header().Get("Location") != "/databases" || !dashboard.signedIn(t) {
		t.Fatalf("sign-in without advertised grants = %d %q, want a dashboard session", response.Code, response.Header().Get("Location"))
	}
	if location := dashboard.do(http.MethodGet, "/login/sso?returnTo=%2Fdatabases").Header().Get("Location"); !strings.HasPrefix(location, issuer.url()+"/authorize?") {
		t.Fatalf("explicit single sign-on = %q, want the issuer's authorization endpoint", location)
	}
}

func TestNativePagesAndSubmissionsDuringTSAuthOutage(t *testing.T) {
	t.Parallel()
	issuer := newTestIssuer(t)
	issuer.server.Close()
	dashboard := newTSAuthDashboard(t, issuer)
	for _, path := range []string{"/login", "/login/code", "/reset-password"} {
		response := dashboard.do(http.MethodGet, path)
		if response.Code != http.StatusOK || response.Header().Get("Location") != "" {
			t.Fatalf("GET %s during an outage = %d %q, want a native page", path, response.Code, response.Header().Get("Location"))
		}
	}
	for _, step := range []struct {
		path string
		form url.Values
	}{
		{"/login", url.Values{"email": {"grace@example.test"}, "password": {"correct horse battery"}}},
		{"/login/code", url.Values{"email": {"grace@example.test"}}},
		{"/login/code/verify", url.Values{"email": {"grace@example.test"}, "code": {"123456"}}},
		{"/reset-password", url.Values{"email": {"grace@example.test"}}},
		{"/reset-password/complete", url.Values{"email": {"grace@example.test"}, "code": {"777777"}, "password": {"a fresh secret 1"}, "confirmPassword": {"a fresh secret 1"}}},
	} {
		response := dashboard.post(step.path, step.form)
		if response.Code != http.StatusServiceUnavailable || response.Header().Get("Location") != "" || dashboard.signedIn(t) {
			t.Fatalf("POST %s during an outage = %d %q, want 503 without a redirect or session", step.path, response.Code, response.Header().Get("Location"))
		}
	}
	response := httptest.NewRecorder()
	err := dashboard.tsAuth.SignUp(response, httptest.NewRequest(http.MethodPost, "/sign-up", nil), dashboard.deps, RegistrationInput{
		DisplayName: "Grace", Email: "grace@example.test", Password: "correct horse battery", ConfirmPassword: "correct horse battery", AcceptedTerms: true,
	}, "/onboarding")
	if !errors.Is(err, core.ErrUnavailable) || response.Header().Get("Location") != "" {
		t.Fatalf("sign-up during an outage = %v %q, want ErrUnavailable without a redirect", err, response.Header().Get("Location"))
	}
	if _, err := dashboard.store.FindByEmail(t.Context(), "grace@example.test"); !errors.Is(err, core.ErrNotFound) {
		t.Fatalf("outage created a local account: %v", err)
	}
}

func TestNativeRefreshWithoutRotationCanBeRevoked(t *testing.T) {
	t.Parallel()
	issuer := newTestIssuer(t)
	issuer.addUser("grace@example.test", "correct horse battery")
	dashboard := newTSAuthDashboard(t, issuer)
	dashboard.post("/login", url.Values{"email": {"grace@example.test"}, "password": {"correct horse battery"}})
	before, _ := dashboard.record(t)
	kept := refreshToken(t, before.Refresh)
	issuer.set(func(i *testIssuer) { i.keepRefresh = true })
	for range 2 {
		dashboard.expire(t)
		if !dashboard.signedIn(t) {
			t.Fatal("refresh without a replacement token ended the native session")
		}
	}
	after, _ := dashboard.record(t)
	if refreshToken(t, after.Refresh) != kept || issuer.refreshCount() != 2 {
		t.Fatal("the native session lost its unrotated refresh token")
	}
	dashboard.do(http.MethodPost, "/api/v1/auth/logout")
	if len(issuer.headless.revoked) != 1 || issuer.headless.revoked[0] != kept || dashboard.signedIn(t) {
		t.Fatalf("logout revoked %v, want the retained refresh token %q", issuer.headless.revoked, kept)
	}
	if _, _, err := dashboard.tsAuth.RefreshSession(t.Context(), after.Refresh); !errors.Is(err, core.ErrUnauthorized) {
		t.Fatalf("refresh after revocation = %v, want ErrUnauthorized", err)
	}
}

// failingUpdates is a store whose account updates fail.
type failingUpdates struct{ core.Store }

func (failingUpdates) Update(context.Context, string, func(*core.Account) error) error {
	return core.ErrUnavailable
}

func TestNativePasswordResetEndsDashboardSessionsOrSaysSo(t *testing.T) {
	t.Parallel()
	store := memstore.New()
	account, err := store.CreateAccount(t.Context(), core.User{Email: "grace@example.test", Name: "Grace", PasswordHash: "unused", Locale: "en", Role: "owner"})
	if err != nil {
		t.Fatal(err)
	}
	// An address without a dashboard account has nothing to end.
	if err := endDashboardSessions(t.Context(), store, "nobody@example.test"); err != nil {
		t.Fatal(err)
	}
	// Every session and token checks the credential version a reset bumps,
	// including a session ts-auth never rechecks.
	if err := endDashboardSessions(t.Context(), store, "grace@example.test"); err != nil {
		t.Fatal(err)
	}
	if _, err := store.SessionPrincipal(t.Context(), account.ID, account.User.ID, account.AuthVersion); !errors.Is(err, core.ErrUnauthorized) {
		t.Fatalf("session from before the reset = %v, want ErrUnauthorized", err)
	}

	// A sign-out that fails is reported, not passed off as done.
	issuer := newTestIssuer(t)
	issuer.addUser("grace@example.test", "correct horse battery")
	dashboard := newTSAuthDashboard(t, issuer, func(s core.Store) core.Store { return failingUpdates{s} })
	dashboard.post("/login", url.Values{"email": {"grace@example.test"}, "password": {"correct horse battery"}})
	dashboard.post("/reset-password", url.Values{"email": {"grace@example.test"}})
	response := dashboard.post("/reset-password/complete", url.Values{
		"email": {"grace@example.test"}, "code": {"777777"}, "password": {"a fresh secret 1"}, "confirmPassword": {"a fresh secret 1"},
	})
	if response.Code != http.StatusServiceUnavailable || !strings.Contains(response.Body.String(), "could not be signed out") || !strings.Contains(response.Body.String(), "Reference: ") {
		t.Fatalf("reset with a failing sign-out = %d, want 503 saying so: %s", response.Code, response.Body)
	}
}

func TestDiscoveryRecheckDoesNotHoldUpNativePages(t *testing.T) {
	t.Parallel()
	issuer := newTestIssuer(t)
	issuer.set(func(i *testIssuer) { i.legacy = true })
	dashboard := newTSAuthDashboard(t, issuer)
	if _, err := dashboard.tsAuth.discover(t.Context()); err != nil {
		t.Fatal(err)
	}
	// Verification can refresh discovery while the native pages stay usable.
	entered, gate := make(chan struct{}, 1), make(chan struct{})
	issuer.set(func(i *testIssuer) { i.discoveryEntered, i.discoveryGate = entered, gate })
	t.Cleanup(func() {
		select {
		case <-gate:
		default:
			close(gate)
		}
	})
	dashboard.tsAuth.mu.Lock()
	dashboard.tsAuth.discovered = time.Now().Add(-time.Hour)
	dashboard.tsAuth.mu.Unlock()
	rechecking := make(chan error, 1)
	go func() {
		_, err := dashboard.tsAuth.discover(t.Context())
		rechecking <- err
	}()
	select {
	case <-entered:
	case <-time.After(5 * time.Second):
		t.Fatal("discovery did not start its recheck")
	}
	for _, path := range []string{"/login", "/login/code", "/reset-password"} {
		done := make(chan int, 1)
		go func() { done <- dashboard.do(http.MethodGet, path).Code }()
		select {
		case code := <-done:
			if code != http.StatusOK {
				t.Fatalf("GET %s during the recheck = %d, want a native page", path, code)
			}
		case <-time.After(5 * time.Second):
			t.Fatalf("GET %s waited for discovery", path)
		}
	}
	close(gate)
	if err := <-rechecking; err != nil {
		t.Fatalf("discovery recheck: %v", err)
	}
}
