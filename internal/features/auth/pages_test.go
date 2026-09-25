package auth

import (
	"net/http"
	"net/http/httptest"
	"net/url"
	"strings"
	"testing"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/platform/session"
)

func TestResetPasswordExplainsUnavailableRecoveryWithoutMutatingPassword(t *testing.T) {
	store := core.NewStore()
	hash, err := HashPassword("a sufficiently long password")
	if err != nil {
		t.Fatal(err)
	}
	account, err := store.CreateAccount(core.User{
		Email: "reset@example.test", Name: "Reset User", PasswordHash: hash,
	})
	if err != nil {
		t.Fatal(err)
	}
	mux := authPageMux(store, session.New(store, session.Options{}))

	get := httptest.NewRecorder()
	mux.ServeHTTP(get, httptest.NewRequest(http.MethodGet, "/reset-password?token=untrusted&sent=1&updated=1", nil))
	if get.Code != http.StatusOK || !strings.Contains(get.Body.String(), "Reset password") ||
		!strings.Contains(get.Body.String(), "does not send recovery emails") ||
		strings.Contains(get.Body.String(), "instructions were sent") ||
		strings.Contains(get.Body.String(), `name="token"`) || strings.Contains(get.Body.String(), `name="newPassword"`) {
		t.Fatalf("GET /reset-password returned an unsafe or misleading page: status=%d body=%s", get.Code, get.Body.String())
	}

	form := url.Values{
		"email":       {account.User.Email},
		"token":       {"untrusted"},
		"newPassword": {"attacker supplied replacement password"},
	}
	post := httptest.NewRecorder()
	request := httptest.NewRequest(http.MethodPost, "/reset-password", strings.NewReader(form.Encode()))
	request.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	mux.ServeHTTP(post, request)
	if post.Code != http.StatusOK || !strings.Contains(post.Body.String(), "No recovery request was processed") ||
		!strings.Contains(post.Body.String(), "no password was changed") || strings.Contains(post.Body.String(), "Password updated") {
		t.Fatalf("POST /reset-password returned a misleading result: status=%d body=%s", post.Code, post.Body.String())
	}
	current, ok := store.Snapshot(account.ID)
	if !ok || current.User.PasswordHash != hash {
		t.Fatalf("reset request changed account password: account=%+v exists=%t", current.User, ok)
	}
}

func TestAgentAndCLIApprovalRejectUnverifiedRequests(t *testing.T) {
	store := core.NewStore()
	hash, err := HashPassword("a sufficiently long password")
	if err != nil {
		t.Fatal(err)
	}
	account, err := store.CreateAccount(core.User{
		Email: "approval@example.test", Name: "Approval User", PasswordHash: hash,
	})
	if err != nil {
		t.Fatal(err)
	}
	mux := authPageMux(store, session.New(store, session.Options{}))

	for _, route := range []struct {
		path string
		name string
	}{
		{path: "/agent/approve", name: "agent"},
		{path: "/cli/approve", name: "CLI"},
	} {
		get := httptest.NewRecorder()
		mux.ServeHTTP(get, httptest.NewRequest(http.MethodGet, route.path+"?code=untrusted&outcome=approved", nil))
		if get.Code != http.StatusOK || !strings.Contains(get.Body.String(), "Approve "+route.name+" access") ||
			!strings.Contains(strings.ToLower(get.Body.String()), "no access was granted") || strings.Contains(get.Body.String(), "Access approved") {
			t.Fatalf("GET %s trusted an unverified request: status=%d body=%s", route.path, get.Code, get.Body.String())
		}

		post := httptest.NewRecorder()
		request := httptest.NewRequest(http.MethodPost, route.path+"/submit", strings.NewReader("action=approve&code=untrusted"))
		request.Header.Set("Content-Type", "application/x-www-form-urlencoded")
		mux.ServeHTTP(post, request)
		if post.Code != http.StatusOK || !strings.Contains(post.Body.String(), "approval was rejected") ||
			!strings.Contains(strings.ToLower(post.Body.String()), "no access was granted") || strings.Contains(post.Body.String(), "Access approved") {
			t.Fatalf("POST %s trusted an unverified request: status=%d body=%s", route.path, post.Code, post.Body.String())
		}
	}
	current, ok := store.Snapshot(account.ID)
	if !ok || current.User.PasswordHash != hash || current.AuthVersion != account.AuthVersion {
		t.Fatalf("approval requests changed account credentials: account=%+v exists=%t", current.User, ok)
	}
}

func TestOAuthConsentUsesBoundedSameOriginReturnAndNeverGrantsUnknownClient(t *testing.T) {
	store := core.NewStore()
	sessions := session.New(store, session.Options{})
	mux := authPageMux(store, sessions)

	anonymous := httptest.NewRecorder()
	mux.ServeHTTP(anonymous, httptest.NewRequest(http.MethodGet, "/oauth/consent?client_id=untrusted&redirect_uri=https%3A%2F%2Fevil.example%2Fcallback", nil))
	if anonymous.Code != http.StatusSeeOther {
		t.Fatalf("anonymous consent status = %d, want redirect", anonymous.Code)
	}
	loginURL, err := url.Parse(anonymous.Header().Get("Location"))
	if err != nil || loginURL.Path != "/login" {
		t.Fatalf("anonymous consent redirect = %q, want /login with returnTo", anonymous.Header().Get("Location"))
	}
	returnTo := loginURL.Query().Get("returnTo")
	if !strings.HasPrefix(returnTo, "/oauth/consent?") || strings.HasPrefix(returnTo, "//") || len(returnTo) > 2048 {
		t.Fatalf("consent return target is not bounded and same-origin: %q", returnTo)
	}

	oversized := httptest.NewRecorder()
	longQuery := strings.Repeat("x", 3000)
	mux.ServeHTTP(oversized, httptest.NewRequest(http.MethodGet, "/oauth/consent?client_id="+longQuery, nil))
	oversizedLogin, err := url.Parse(oversized.Header().Get("Location"))
	if err != nil || oversizedLogin.Path != "/login" || oversizedLogin.Query().Get("returnTo") != "/oauth/consent" {
		t.Fatalf("oversized consent redirect = %q, want bounded consent path", oversized.Header().Get("Location"))
	}

	account, err := store.CreateAccount(core.User{
		Email: "oauth@example.test", Name: "OAuth User", PasswordHash: "test-password-hash",
	})
	if err != nil {
		t.Fatal(err)
	}
	loginResponse := httptest.NewRecorder()
	if err := sessions.Start(loginResponse, httptest.NewRequest(http.MethodGet, "/", nil), account.Principal()); err != nil {
		t.Fatal(err)
	}
	authenticated := httptest.NewRequest(http.MethodGet, "/oauth/consent?client_id=untrusted&scope=admin", nil)
	for _, cookie := range loginResponse.Result().Cookies() {
		authenticated.AddCookie(cookie)
	}
	consent := httptest.NewRecorder()
	mux.ServeHTTP(consent, authenticated)
	if consent.Code != http.StatusOK || !strings.Contains(consent.Body.String(), "cannot verify OAuth authorization requests") ||
		!strings.Contains(consent.Body.String(), "No access was granted") || strings.Contains(consent.Body.String(), "untrusted") ||
		strings.Contains(consent.Body.String(), "Access approved") {
		t.Fatalf("unknown OAuth client was trusted: status=%d body=%s", consent.Code, consent.Body.String())
	}

	post := httptest.NewRecorder()
	approval := httptest.NewRequest(http.MethodPost, "/oauth/consent/submit", strings.NewReader("client_id=untrusted&action=approve"))
	approval.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	mux.ServeHTTP(post, approval)
	if post.Code != http.StatusOK || !strings.Contains(post.Body.String(), "authorization request was rejected") ||
		!strings.Contains(post.Body.String(), "No access was granted") || strings.Contains(post.Body.String(), "Access approved") {
		t.Fatalf("unknown OAuth consent was approved: status=%d body=%s", post.Code, post.Body.String())
	}
}

func TestLoginPreservesOnlySafeReturnTargets(t *testing.T) {
	store := core.NewStore()
	hash, err := HashPassword("a sufficiently long password")
	if err != nil {
		t.Fatal(err)
	}
	account, err := store.CreateAccount(core.User{
		Email: "login@example.test", Name: "Login User", PasswordHash: hash,
	})
	if err != nil {
		t.Fatal(err)
	}
	sessions := session.New(store, session.Options{})
	mux := authPageMux(store, sessions)

	login := httptest.NewRecorder()
	mux.ServeHTTP(login, httptest.NewRequest(http.MethodGet, "/login?returnTo=%2Foauth%2Fconsent%3Fclient_id%3Ddemo", nil))
	if !strings.Contains(login.Body.String(), `name="redirectTo" value="/oauth/consent?client_id=demo"`) {
		t.Fatalf("login page did not preserve the safe return path: %s", login.Body.String())
	}
	unsafe := httptest.NewRecorder()
	mux.ServeHTTP(unsafe, httptest.NewRequest(http.MethodGet, "/login?returnTo=%2F%2Fevil.example", nil))
	if !strings.Contains(unsafe.Body.String(), `name="redirectTo" value="/"`) {
		t.Fatalf("login page kept an unsafe return path: %s", unsafe.Body.String())
	}

	form := url.Values{
		"email":      {account.User.Email},
		"password":   {"a sufficiently long password"},
		"redirectTo": {"/oauth/consent?client_id=demo"},
	}
	request := httptest.NewRequest(http.MethodPost, "/api/v1/auth/password/login", strings.NewReader(form.Encode()))
	request.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	response := httptest.NewRecorder()
	mux.ServeHTTP(response, request)
	if response.Code != http.StatusSeeOther || response.Header().Get("Location") != "/oauth/consent?client_id=demo" {
		t.Fatalf("login redirect = (%d, %q), want bounded consent path", response.Code, response.Header().Get("Location"))
	}
}

func TestSuccessfulFormLoginDefaultsToOnboardingOnlyWhenIncomplete(t *testing.T) {
	const password = "a sufficiently long password"

	for _, test := range []struct {
		name                string
		onboardingCompleted bool
		wantLocation        string
	}{
		{name: "incomplete account", wantLocation: "/onboarding"},
		{name: "complete account", onboardingCompleted: true, wantLocation: "/"},
	} {
		t.Run(test.name, func(t *testing.T) {
			store := core.NewStore()
			hash, err := HashPassword(password)
			if err != nil {
				t.Fatal(err)
			}
			account, err := store.CreateAccount(core.User{
				Email: "resume@example.test", Name: "Resume User", PasswordHash: hash,
			})
			if err != nil {
				t.Fatal(err)
			}
			if test.onboardingCompleted {
				if err := store.Update(account.ID, func(account *core.Account) error {
					account.OnboardingCompleted = true
					return nil
				}); err != nil {
					t.Fatal(err)
				}
			}

			form := url.Values{
				"email":      {account.User.Email},
				"password":   {password},
				"redirectTo": {"/"},
			}
			request := httptest.NewRequest(http.MethodPost, "/api/v1/auth/password/login", strings.NewReader(form.Encode()))
			request.Header.Set("Content-Type", "application/x-www-form-urlencoded")
			response := httptest.NewRecorder()
			authPageMux(store, session.New(store, session.Options{})).ServeHTTP(response, request)
			if response.Code != http.StatusSeeOther || response.Header().Get("Location") != test.wantLocation {
				t.Fatalf("form login redirect = (%d, %q), want %q", response.Code, response.Header().Get("Location"), test.wantLocation)
			}
		})
	}
}

func TestAuthenticatedLoginRedirectsBasedOnOnboardingCompletion(t *testing.T) {
	for _, test := range []struct {
		name                string
		onboardingCompleted bool
		wantLocation        string
	}{
		{name: "incomplete account", wantLocation: "/onboarding"},
		{name: "complete account", onboardingCompleted: true, wantLocation: "/"},
	} {
		t.Run(test.name, func(t *testing.T) {
			store := core.NewStore()
			account, err := store.CreateAccount(core.User{
				Email: "resume@example.test", Name: "Resume User", PasswordHash: "test-password-hash",
			})
			if err != nil {
				t.Fatal(err)
			}
			if test.onboardingCompleted {
				if err := store.Update(account.ID, func(account *core.Account) error {
					account.OnboardingCompleted = true
					return nil
				}); err != nil {
					t.Fatal(err)
				}
			}

			sessions := session.New(store, session.Options{})
			loginResponse := httptest.NewRecorder()
			if err := sessions.Start(loginResponse, httptest.NewRequest(http.MethodGet, "/login", nil), account.Principal()); err != nil {
				t.Fatal(err)
			}
			request := httptest.NewRequest(http.MethodGet, "/login", nil)
			for _, cookie := range loginResponse.Result().Cookies() {
				request.AddCookie(cookie)
			}
			response := httptest.NewRecorder()
			authPageMux(store, sessions).ServeHTTP(response, request)
			if response.Code != http.StatusSeeOther || response.Header().Get("Location") != test.wantLocation {
				t.Fatalf("authenticated GET /login redirect = (%d, %q), want %q", response.Code, response.Header().Get("Location"), test.wantLocation)
			}
		})
	}
}

func TestFailedFormLoginPreservesSafeReturnTargetForRetry(t *testing.T) {
	const password = "a sufficiently long password"
	const checkoutReturn = "/billing/checkout/return?session_id=cs_test_123"

	for _, test := range []struct {
		name                    string
		password                string
		returnTo                string
		wantReturnTo            string
		wantRetryLocation       string
		failCredentialStartOnce bool
	}{
		{
			name:              "invalid credentials retain checkout return",
			password:          "wrong password",
			returnTo:          checkoutReturn,
			wantReturnTo:      checkoutReturn,
			wantRetryLocation: checkoutReturn,
		},
		{
			name:                    "credential race retains checkout return",
			password:                password,
			returnTo:                checkoutReturn,
			wantReturnTo:            checkoutReturn,
			wantRetryLocation:       checkoutReturn,
			failCredentialStartOnce: true,
		},
		{
			name:              "network path is rejected",
			password:          "wrong password",
			returnTo:          "//attacker.example/path",
			wantReturnTo:      "/",
			wantRetryLocation: "/onboarding",
		},
		{
			name:              "absolute URL is rejected",
			password:          "wrong password",
			returnTo:          "https://attacker.example/path",
			wantReturnTo:      "/",
			wantRetryLocation: "/onboarding",
		},
	} {
		t.Run(test.name, func(t *testing.T) {
			store := core.NewStore()
			hash, err := HashPassword(password)
			if err != nil {
				t.Fatal(err)
			}
			account, err := store.CreateAccount(core.User{
				Email: "retry@example.test", Name: "Retry User", PasswordHash: hash,
			})
			if err != nil {
				t.Fatal(err)
			}

			sessions := &credentialLoginSessions{
				Sessions: session.New(store, session.Options{}),
				failNext: test.failCredentialStartOnce,
			}
			mux := authPageMux(store, sessions)
			form := url.Values{
				"email":      {account.User.Email},
				"password":   {test.password},
				"redirectTo": {test.returnTo},
			}
			failureRequest := httptest.NewRequest(http.MethodPost, "/api/v1/auth/password/login", strings.NewReader(form.Encode()))
			failureRequest.Header.Set("Content-Type", "application/x-www-form-urlencoded")
			failure := httptest.NewRecorder()
			mux.ServeHTTP(failure, failureRequest)
			if failure.Code != http.StatusSeeOther {
				t.Fatalf("failed login status = %d, want redirect; body=%s", failure.Code, failure.Body.String())
			}
			loginURL, err := url.Parse(failure.Header().Get("Location"))
			if err != nil || loginURL.Path != "/login" || loginURL.Query().Get("returnTo") != test.wantReturnTo {
				t.Fatalf("failed login location = %q, want safe returnTo %q", failure.Header().Get("Location"), test.wantReturnTo)
			}

			retryPage := httptest.NewRecorder()
			mux.ServeHTTP(retryPage, httptest.NewRequest(http.MethodGet, loginURL.RequestURI(), nil))
			hiddenTarget := `name="redirectTo" value="` + test.wantReturnTo + `"`
			if retryPage.Code != http.StatusOK || !strings.Contains(retryPage.Body.String(), hiddenTarget) {
				t.Fatalf("retry login form did not preserve %q: status=%d body=%s", test.wantReturnTo, retryPage.Code, retryPage.Body.String())
			}

			retryForm := url.Values{
				"email":      {account.User.Email},
				"password":   {password},
				"redirectTo": {loginURL.Query().Get("returnTo")},
			}
			retryRequest := httptest.NewRequest(http.MethodPost, "/api/v1/auth/password/login", strings.NewReader(retryForm.Encode()))
			retryRequest.Header.Set("Content-Type", "application/x-www-form-urlencoded")
			retry := httptest.NewRecorder()
			mux.ServeHTTP(retry, retryRequest)
			if retry.Code != http.StatusSeeOther || retry.Header().Get("Location") != test.wantRetryLocation {
				t.Fatalf("retry login location = (%d, %q), want %q", retry.Code, retry.Header().Get("Location"), test.wantRetryLocation)
			}
		})
	}
}

type credentialLoginSessions struct {
	core.Sessions
	failNext bool
}

func (s *credentialLoginSessions) StartIfPasswordHash(w http.ResponseWriter, r *http.Request, principal core.Principal, passwordHash string) error {
	if s.failNext {
		s.failNext = false
		return core.ErrConflict
	}
	return s.Sessions.(core.CredentialSessions).StartIfPasswordHash(w, r, principal, passwordHash)
}

func authPageMux(store *core.Store, sessions core.Sessions) *http.ServeMux {
	mux := http.NewServeMux()
	Mount(mux, core.Deps{Store: store, Sessions: sessions, Render: authPageTestRenderer{}})
	return mux
}

type authPageTestRenderer struct{}

func (authPageTestRenderer) Page(w http.ResponseWriter, r *http.Request, page core.Page) error {
	if page.View == nil {
		return core.ErrInvalid
	}
	return page.View(page).Render(r.Context(), w)
}

func (authPageTestRenderer) Fragment(http.ResponseWriter, *http.Request, core.Fragment) error {
	return nil
}
