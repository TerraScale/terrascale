package auth

import (
	"context"
	"net/http"
	"net/url"
	"strings"
	"sync"
	"testing"
	"time"

	"terrascale.tech/dashboard/internal/platform/throttle"
)

type signUpClock struct {
	throttle.Store
	mu  sync.Mutex
	now time.Time
}

func (c *signUpClock) Reserve(ctx context.Context, _ time.Time, buckets []throttle.Bucket) (time.Duration, bool, error) {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.Store.Reserve(ctx, c.now, buckets)
}

func (c *signUpClock) advance(wait time.Duration) {
	c.mu.Lock()
	c.now = c.now.Add(wait)
	c.mu.Unlock()
}

func signUpResendDashboard(t *testing.T, issuer *testIssuer) (*tsAuthDashboard, *signUpClock) {
	t.Helper()
	d := newTSAuthDashboard(t, issuer)
	clock := &signUpClock{Store: throttle.NewMemoryStore(), now: time.Now()}
	d.deps.SignUpMail = throttle.New(throttle.Options{
		Store: clock, KeyPrefix: "signup-mail ", Account: throttle.Budget{Burst: 1, Interval: time.Minute},
	})
	d.mux = http.NewServeMux()
	Mount(d.mux, d.deps, d.tsAuth)
	d.mux.HandleFunc("POST /sign-up", func(w http.ResponseWriter, r *http.Request) {
		input := RegistrationInput{
			DisplayName: "Ada Lovelace", Email: "ada@example.test", Password: "analytical engine",
			ConfirmPassword: "analytical engine", AcceptedTerms: true,
		}
		if err := d.tsAuth.SignUp(w, r, d.deps, input, "/onboarding?plan=starter"); err != nil {
			http.Error(w, err.Error(), http.StatusUnprocessableEntity)
		}
	})
	return d, clock
}

func TestSignUpCodeResendWaitsOneMinuteAndCompletesRegistration(t *testing.T) {
	t.Parallel()
	issuer := newTestIssuer(t)
	d, clock := signUpResendDashboard(t, issuer)
	response := d.post("/sign-up", nil)
	if response.Code != http.StatusOK {
		t.Fatalf("sign-up = %d: %s", response.Code, response.Body)
	}
	for _, marker := range []string{`action="/login/code/resend"`, `ts-btn-outline`, `Resend code (60s)`} {
		if !strings.Contains(response.Body.String(), marker) {
			t.Fatalf("verification page has no %s", marker)
		}
	}
	if strings.Contains(response.Body.String(), "Sign up again") || strings.Contains(response.Body.String(), "analytical engine") {
		t.Fatal("verification page exposes the password or old sign-up instruction")
	}
	initialToken := d.cookies[d.tsAuth.signUpCookie()].Value
	for _, wait := range []time.Duration{0, 59 * time.Second} {
		clock.advance(wait)
		response = d.post("/login/code/resend", nil)
		if response.Code != http.StatusTooManyRequests || issuer.headlessCalls(headlessSignUp) != 1 {
			t.Fatalf("resend before a minute = %d after %d sends", response.Code, issuer.headlessCalls(headlessSignUp))
		}
		if response.Header().Get("Retry-After") == "" || !strings.Contains(response.Body.String(), "Verify your email") {
			t.Fatal("blocked resend did not preserve verification and advertise the remaining wait")
		}
	}
	clock.advance(time.Second)
	response = d.post("/login/code/resend", url.Values{"email": {"other@example.test"}, "password": {"forged"}})
	if response.Code != http.StatusOK || issuer.headlessCalls(headlessSignUp) != 2 || !strings.Contains(response.Body.String(), "Check your email for a new code.") {
		t.Fatalf("resend at 60s = %d after %d sends: %s", response.Code, issuer.headlessCalls(headlessSignUp), response.Body)
	}
	if d.cookies[d.tsAuth.signUpCookie()].Value == initialToken {
		t.Fatal("resend did not rotate the pending token")
	}
	if response := d.post("/login/code/resend", nil); response.Code != http.StatusTooManyRequests || issuer.headlessCalls(headlessSignUp) != 2 {
		t.Fatal("a second resend bypassed the fresh cooldown")
	}
	verify := url.Values{"email": {"ada@example.test"}, "signUp": {"1"}, "name": {"Ada Lovelace"}, "redirectTo": {"/onboarding?plan=starter"}, "code": {"654321"}}
	response = d.post("/login/code/verify", verify)
	if response.Code != http.StatusUnauthorized || !strings.Contains(response.Body.String(), "Resend code (") {
		t.Fatal("old code remained valid or the rejected code lost the cooldown")
	}
	verify.Set("code", "654322")
	response = d.post("/login/code/verify", verify)
	if response.Code != http.StatusSeeOther || response.Header().Get("Location") != "/onboarding?plan=starter" || !d.signedIn(t) {
		t.Fatalf("new code = %d → %q, want registration continuing onboarding", response.Code, response.Header().Get("Location"))
	}
	for _, name := range []string{d.tsAuth.signUpCookie(), d.tsAuth.signUpRetryCookie()} {
		if _, ok := d.cookies[name]; ok {
			t.Fatalf("verification left %s behind", name)
		}
	}
	d.do(http.MethodPost, "/api/v1/auth/logout")
	if response := d.post("/login", url.Values{"email": {"ada@example.test"}, "password": {"analytical engine"}}); response.Code != http.StatusSeeOther {
		t.Fatalf("original password after resend = %d", response.Code)
	}
}

func TestSignUpCodeResendRejectsInvalidRetryCookies(t *testing.T) {
	t.Parallel()
	for _, variant := range []string{"missing", "tampered", "expired", "another-token", "another-client"} {
		t.Run(variant, func(t *testing.T) {
			issuer := newTestIssuer(t)
			d, clock := signUpResendDashboard(t, issuer)
			if response := d.post("/sign-up", nil); response.Code != http.StatusOK {
				t.Fatal("sign-up failed")
			}
			clock.advance(time.Minute)
			cookie := d.cookies[d.tsAuth.signUpRetryCookie()]
			if !cookie.HttpOnly || cookie.MaxAge != 3600 || cookie.SameSite != http.SameSiteLaxMode || strings.Contains(cookie.Value, "analytical engine") {
				t.Fatal("retry cookie does not protect the sign-up request")
			}
			switch variant {
			case "missing":
				delete(d.cookies, cookie.Name)
			case "tampered":
				cookie.Value = "forged" + cookie.Value[6:]
			case "expired":
				retry := signUpRetry{Email: "ada@example.test", Token: d.cookies[d.tsAuth.signUpCookie()].Value, Expires: time.Now().Add(-time.Second)}
				var err error
				cookie.Value, err = d.tsAuth.sealSignUpRetry(retry)
				if err != nil {
					t.Fatal(err)
				}
			case "another-token":
				d.cookies[d.tsAuth.signUpCookie()].Value = "another-request"
			case "another-client":
				d.tsAuth.cfg.ClientSecret = "another-client-secret"
			}
			response := d.post("/login/code/resend", nil)
			if response.Code != http.StatusBadRequest || issuer.headlessCalls(headlessSignUp) != 1 {
				t.Fatalf("%s cookie resend = %d after %d sends", variant, response.Code, issuer.headlessCalls(headlessSignUp))
			}
		})
	}
}

func TestSignUpCodeResendKeepsVerificationAfterMailFailure(t *testing.T) {
	t.Parallel()
	issuer := newTestIssuer(t)
	d, clock := signUpResendDashboard(t, issuer)
	d.tsAuth.cfg.Secure = true
	if response := d.post("/sign-up", nil); response.Code != http.StatusOK {
		t.Fatal("sign-up failed")
	}
	cookie := d.cookies[d.tsAuth.signUpRetryCookie()]
	if !cookie.Secure || !strings.HasPrefix(cookie.Name, "__Host-") {
		t.Fatal("production retry cookie is not restricted to its HTTPS host")
	}
	clock.advance(time.Minute)
	issuer.set(func(i *testIssuer) { i.headless.status = http.StatusServiceUnavailable })
	response := d.post("/login/code/resend", nil)
	if response.Code != http.StatusServiceUnavailable || !strings.Contains(response.Body.String(), "Resend code (60s)") {
		t.Fatalf("failed resend = %d, want verification with a fresh cooldown", response.Code)
	}
	issuer.set(func(i *testIssuer) { i.headless.status = 0 })
	if response := d.post("/login/code/resend", nil); response.Code != http.StatusTooManyRequests {
		t.Fatal("mail failure bypassed the reserved cooldown")
	}
	response = d.post("/login/code/verify", url.Values{"email": {"ada@example.test"}, "signUp": {"1"}, "code": {"654321"}})
	if response.Code != http.StatusSeeOther || !d.signedIn(t) {
		t.Fatalf("original code after failed resend = %d, want successful verification", response.Code)
	}
}
