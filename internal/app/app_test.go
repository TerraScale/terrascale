package app

import (
	"net/http"
	"net/http/httptest"
	"net/url"
	"regexp"
	"strings"
	"testing"

	"terrascale.tech/dashboard/internal/platform/config"
)

func TestMountedCallerAndAssets(t *testing.T) {
	application, err := New(config.Config{Demo: true})
	if err != nil {
		t.Fatal(err)
	}
	for _, target := range []string{"/health/ready", "/login", "/onboarding", "/css/terrascale.css", "/js/datastar.js", "/brand/logo-terrascale-auth.svg"} {
		request := httptest.NewRequest(http.MethodGet, "http://dashboard.test"+target, nil)
		response := httptest.NewRecorder()
		application.Handler.ServeHTTP(response, request)
		if response.Code != http.StatusOK {
			t.Errorf("GET %s: status %d body %q", target, response.Code, response.Body.String())
		}
		if target == "/login" {
			if !strings.Contains(response.Body.String(), "data-nonce=") || !strings.Contains(response.Header().Get("Content-Security-Policy"), "'nonce-") {
				t.Error("login page missing matching CSP nonce contract")
			}
		}
	}
}

func TestRegistrationToOverviewThroughMountedApp(t *testing.T) {
	application, err := New(config.Config{Demo: true})
	if err != nil {
		t.Fatal(err)
	}
	get := httptest.NewRequest(http.MethodGet, "http://dashboard.test/onboarding", nil)
	getResult := httptest.NewRecorder()
	application.Handler.ServeHTTP(getResult, get)
	if getResult.Code != http.StatusOK {
		t.Fatalf("registration page: %d", getResult.Code)
	}
	match := regexp.MustCompile(`name="_csrf" value="([^"]+)"`).FindStringSubmatch(getResult.Body.String())
	if len(match) != 2 {
		t.Fatal("registration form lacks CSRF token")
	}
	csrfCookie := getResult.Result().Cookies()[0]
	post := func(path string, values url.Values, sessionCookie *http.Cookie) *httptest.ResponseRecorder {
		t.Helper()
		values.Set("_csrf", match[1])
		r := httptest.NewRequest(http.MethodPost, "http://dashboard.test"+path, strings.NewReader(values.Encode()))
		r.Header.Set("Content-Type", "application/x-www-form-urlencoded")
		r.Header.Set("Origin", "http://dashboard.test")
		r.AddCookie(csrfCookie)
		if sessionCookie != nil {
			r.AddCookie(sessionCookie)
		}
		w := httptest.NewRecorder()
		application.Handler.ServeHTTP(w, r)
		return w
	}
	registered := post("/onboarding/register", url.Values{
		"displayName": {"Integration User"}, "email": {"user@example.com"},
		"password": {"strong-password-123"}, "confirmPassword": {"strong-password-123"}, "terms": {"true"},
	}, nil)
	if registered.Code != http.StatusSeeOther {
		t.Fatalf("registration: %d %s", registered.Code, registered.Body.String())
	}
	var sessionCookie *http.Cookie
	for _, cookie := range registered.Result().Cookies() {
		if cookie.Name == "ts_dashboard_session" {
			sessionCookie = cookie
		}
	}
	if sessionCookie == nil {
		t.Fatal("registration did not issue session")
	}
	payment := post("/onboarding/step", url.Values{"step": {"payment-method"}, "bypass": {"1"}}, sessionCookie)
	if payment.Code != http.StatusSeeOther {
		t.Fatalf("payment step: %d %s", payment.Code, payment.Body.String())
	}
	project := post("/onboarding/step", url.Values{"step": {"project"}, "name": {"Personal"}, "region": {"GRU"}}, sessionCookie)
	if project.Code != http.StatusSeeOther || project.Header().Get("Location") != "/" {
		t.Fatalf("project step: %d %s", project.Code, project.Header().Get("Location"))
	}
	account, exists := application.Store.FindByEmail("user@example.com")
	if !exists || !account.OnboardingCompleted || account.ActiveProjectID == "" {
		t.Fatal("onboarding did not commit account/project")
	}
	overviewRequest := httptest.NewRequest(http.MethodGet, "http://dashboard.test/", nil)
	overviewRequest.AddCookie(sessionCookie)
	overviewResponse := httptest.NewRecorder()
	application.Handler.ServeHTTP(overviewResponse, overviewRequest)
	if overviewResponse.Code != http.StatusOK {
		t.Fatalf("overview: %d %s", overviewResponse.Code, overviewResponse.Body.String())
	}
}

func TestDemoAdminRequiresExplicitCredentials(t *testing.T) {
	plain, err := New(config.Config{Demo: true})
	if err != nil {
		t.Fatal(err)
	}
	if _, ok := plain.Store.FindByEmail("admin@example.com"); ok {
		t.Fatal("unconfigured process exposed platform administration")
	}
	if _, err := New(config.Config{Demo: true, AdminEmail: "admin@example.com"}); err == nil {
		t.Fatal("partial admin credentials accepted")
	}
	seeded, err := New(config.Config{
		Demo: true, AdminEmail: "admin@example.com", AdminPassword: "strong-demo-password",
	})
	if err != nil {
		t.Fatal(err)
	}
	a, ok := seeded.Store.FindByEmail("admin@example.com")
	if !ok || !a.User.PlatformAdmin || !a.OnboardingCompleted || a.User.PasswordHash == "strong-demo-password" {
		t.Fatal("explicit administrator not safely provisioned")
	}
	if _, err := seeded.Store.PlatformAccounts(a.Principal()); err != nil {
		t.Fatalf("provisioned admin denied: %v", err)
	}
	adminRequest := httptest.NewRequest(http.MethodGet, "http://dashboard.test/admin/users", nil)
	sessionResponse := httptest.NewRecorder()
	if err := seeded.Deps.Sessions.Start(sessionResponse, adminRequest, a.Principal()); err != nil {
		t.Fatalf("admin session: %v", err)
	}
	for _, cookie := range sessionResponse.Result().Cookies() {
		adminRequest.AddCookie(cookie)
	}
	adminResponse := httptest.NewRecorder()
	seeded.Handler.ServeHTTP(adminResponse, adminRequest)
	if adminResponse.Code != http.StatusOK || !strings.Contains(adminResponse.Body.String(), "admin@example.com") {
		t.Fatalf("mounted admin users page: %d %s", adminResponse.Code, adminResponse.Body.String())
	}
}
