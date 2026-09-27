package auth

import (
	"net/http"
	"net/http/httptest"
	"net/url"
	"strconv"
	"strings"
	"testing"
	"time"

	"terrascale.tech/dashboard/internal/platform/throttle"
)

func TestLoginThrottlesOnlyFailedAttempts(t *testing.T) {
	t.Parallel()
	mux := newLoginMux(t, throttle.Options{
		Client:  throttle.Budget{Burst: 1, Interval: time.Minute},
		Account: throttle.Budget{Burst: 1, Interval: time.Minute},
	})
	signIn := func(client, email, password string) *httptest.ResponseRecorder {
		t.Helper()
		body := `{"email":"` + email + `","password":"` + password + `"}`
		request := httptest.NewRequest(http.MethodPost, "/api/v1/auth/password/login", strings.NewReader(body))
		request.Header.Set("Content-Type", "application/json")
		request.RemoteAddr = client + ":4000"
		response := httptest.NewRecorder()
		mux.ServeHTTP(response, request)
		return response
	}
	// The wait is at most one interval, less the time the checks took.
	refused := func(response *httptest.ResponseRecorder) bool {
		wait, err := strconv.Atoi(response.Header().Get("Retry-After"))
		return response.Code == http.StatusTooManyRequests && err == nil && wait > 0 && wait <= 60
	}

	for i := range 2 {
		if response := signIn("192.0.2.1", testEmail, testPassword); response.Code != http.StatusOK {
			t.Fatalf("correct sign-in %d = %d, want 200", i+1, response.Code)
		}
	}

	// A failure spends the account's budget for every client, and a correct
	// password cannot get past it.
	if response := signIn("192.0.2.2", strings.ToUpper(testEmail), "wrong-password"); response.Code != http.StatusUnauthorized {
		t.Fatalf("failed sign-in = %d, want 401", response.Code)
	}
	response := signIn("192.0.2.3", testEmail, testPassword)
	if !refused(response) || !strings.Contains(response.Body.String(), `"too_many_attempts"`) {
		t.Fatalf("sign-in past the account budget = %d %q (Retry-After %q), want 429 too_many_attempts",
			response.Code, response.Body.String(), response.Header().Get("Retry-After"))
	}

	// A failure for an unregistered email spends the client's budget for
	// every account.
	if response := signIn("192.0.2.5", "nobody@example.test", "wrong-password"); response.Code != http.StatusUnauthorized {
		t.Fatalf("failed sign-in for an unregistered email = %d, want 401", response.Code)
	}
	if response := signIn("192.0.2.5", "someone@example.test", testPassword); !refused(response) {
		t.Fatalf("sign-in past the client budget = %d (Retry-After %q), want 429", response.Code, response.Header().Get("Retry-After"))
	}

	// The browser form shows the wait and keeps what was typed, still under
	// the page's Content-Security-Policy.
	form := url.Values{"email": {"someone@example.test"}, "password": {testPassword}, "redirectTo": {"/databases"}}
	request := httptest.NewRequest(http.MethodPost, "/login", strings.NewReader(form.Encode()))
	request.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	request.RemoteAddr = "192.0.2.5:4000"
	page := httptest.NewRecorder()
	mux.ServeHTTP(page, request)
	body := page.Body.String()
	if !refused(page) || !strings.Contains(body, "Too many sign-in attempts. Try again in ") ||
		!strings.Contains(body, `value="someone@example.test"`) || !strings.Contains(body, `name="redirectTo" value="/databases"`) ||
		page.Header().Get("Content-Security-Policy") == "" {
		t.Fatalf("throttled form sign-in = %d (Retry-After %q, CSP %q), want the sign-in page with the wait",
			page.Code, page.Header().Get("Retry-After"), page.Header().Get("Content-Security-Policy"))
	}
}
