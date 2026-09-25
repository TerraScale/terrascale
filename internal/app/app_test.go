package app

import (
	"net/http"
	"net/http/httptest"
	"net/url"
	"regexp"
	"strings"
	"testing"

	"terrascale.tech/dashboard/internal/features/auth"
	"terrascale.tech/dashboard/internal/platform/config"
	static "terrascale.tech/dashboard/web/static"
)

const originalDemoPassword = "terrascale"

func TestMountedCallerAndAssets(t *testing.T) {
	application, err := New(config.Config{Demo: true})
	if err != nil {
		t.Fatal(err)
	}
	for _, target := range []string{"/health/ready", "/login", "/onboarding", "/css/terrascale.css", "/brand/logo-terrascale-auth.svg"} {
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

func TestUnauthenticatedBillingDeepLinkSurvivesLoginPage(t *testing.T) {
	application, err := New(config.Config{Demo: true})
	if err != nil {
		t.Fatal(err)
	}
	billing := httptest.NewRecorder()
	application.Handler.ServeHTTP(billing, httptest.NewRequest(http.MethodGet, "http://dashboard.test/billing?payment=pending", nil))
	if billing.Code != http.StatusSeeOther {
		t.Fatalf("billing redirect status = %d, want 303", billing.Code)
	}
	location := billing.Header().Get("Location")
	parsed, err := url.Parse(location)
	if err != nil || parsed.Path != "/login" || parsed.Query().Get("returnTo") != "/billing?payment=pending" {
		t.Fatalf("billing login location = %q, parse error = %v", location, err)
	}
	login := httptest.NewRecorder()
	application.Handler.ServeHTTP(login, httptest.NewRequest(http.MethodGet, "http://dashboard.test"+location, nil))
	if login.Code != http.StatusOK || !strings.Contains(login.Body.String(), `name="redirectTo" value="/billing?payment=pending"`) {
		t.Fatalf("login lost billing return destination: status %d, body %q", login.Code, login.Body.String())
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
	if !exists || !account.OnboardingCompleted || account.ActiveProjectID == "" || account.User.PlatformAdmin {
		t.Fatal("onboarding did not commit account/project")
	}
	overviewRequest := httptest.NewRequest(http.MethodGet, "http://dashboard.test/", nil)
	overviewRequest.AddCookie(sessionCookie)
	overviewResponse := httptest.NewRecorder()
	application.Handler.ServeHTTP(overviewResponse, overviewRequest)
	if overviewResponse.Code != http.StatusOK {
		t.Fatalf("overview: %d %s", overviewResponse.Code, overviewResponse.Body.String())
	}
	for _, route := range []struct {
		path string
		want string
	}{
		{path: "/billing", want: "Plans and billing"},
		{path: "/subscriptions", want: "Shared result configuration"},
	} {
		request := httptest.NewRequest(http.MethodGet, "http://dashboard.test"+route.path, nil)
		request.AddCookie(sessionCookie)
		response := httptest.NewRecorder()
		application.Handler.ServeHTTP(response, request)
		if response.Code != http.StatusOK || !strings.Contains(response.Body.String(), route.want) {
			t.Fatalf("GET %s: status %d, missing %q, body %q", route.path, response.Code, route.want, response.Body.String())
		}
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

func TestDemoAccountUsesOriginalCredentialThroughMountedLogin(t *testing.T) {
	application, err := New(config.Config{Demo: true})
	if err != nil {
		t.Fatal(err)
	}
	account, ok := application.Store.FindByEmail(demoAccountEmail)
	if !ok {
		t.Fatal("original demo account is missing")
	}
	if account.User.Name != "Mario Kraus" || account.User.Role != "owner" || !account.User.PlatformAdmin ||
		!account.OnboardingCompleted || account.OnboardingStep != "complete" {
		t.Fatal("original demo account profile or setup is incorrect")
	}
	org, ok := account.Organizations[account.ActiveOrganizationID]
	if !ok || org.Name != "TerraScale Labs" || org.Kind != "business" || org.BillingStatus != "active" ||
		org.OwnerUserID != account.User.ID || org.Projects[account.ActiveProjectID].Name != "core-platform" {
		t.Fatal("original demo organization or project is unavailable")
	}
	if account.User.PasswordHash == originalDemoPassword ||
		!auth.VerifyPassword(account.User.PasswordHash, originalDemoPassword) ||
		auth.VerifyPassword(account.User.PasswordHash, "incorrect-password") {
		t.Fatal("original demo credential was not securely provisioned")
	}

	loginPage := httptest.NewRecorder()
	application.Handler.ServeHTTP(loginPage, httptest.NewRequest(http.MethodGet, "http://dashboard.test/login", nil))
	if loginPage.Code != http.StatusOK {
		t.Fatalf("login page returned %d", loginPage.Code)
	}
	match := regexp.MustCompile(`name="_csrf" value="([^"]+)"`).FindStringSubmatch(loginPage.Body.String())
	if len(match) != 2 {
		t.Fatal("login form lacks CSRF token")
	}
	var csrfCookie *http.Cookie
	for _, cookie := range loginPage.Result().Cookies() {
		if cookie.Name == "ts_dashboard_csrf" {
			csrfCookie = cookie
		}
	}
	if csrfCookie == nil {
		t.Fatal("login did not issue CSRF cookie")
	}
	login := func(password string) *httptest.ResponseRecorder {
		t.Helper()
		values := url.Values{"email": {demoAccountEmail}, "password": {password}, "_csrf": {match[1]}}
		request := httptest.NewRequest(http.MethodPost, "http://dashboard.test/api/v1/auth/password/login", strings.NewReader(values.Encode()))
		request.Header.Set("Content-Type", "application/x-www-form-urlencoded")
		request.Header.Set("Origin", "http://dashboard.test")
		request.AddCookie(csrfCookie)
		response := httptest.NewRecorder()
		application.Handler.ServeHTTP(response, request)
		return response
	}
	wrong := login("incorrect-password")
	if wrong.Code != http.StatusSeeOther || !strings.HasPrefix(wrong.Header().Get("Location"), "/login") {
		t.Fatalf("wrong password login returned %d at %q", wrong.Code, wrong.Header().Get("Location"))
	}
	for _, cookie := range wrong.Result().Cookies() {
		if cookie.Name == "ts_dashboard_session" {
			t.Fatal("wrong password issued a session")
		}
	}
	correct := login(originalDemoPassword)
	if correct.Code != http.StatusSeeOther || correct.Header().Get("Location") != "/" {
		t.Fatalf("original password login returned %d at %q", correct.Code, correct.Header().Get("Location"))
	}
	var sessionCookie *http.Cookie
	for _, cookie := range correct.Result().Cookies() {
		if cookie.Name == "ts_dashboard_session" {
			sessionCookie = cookie
		}
	}
	if sessionCookie == nil {
		t.Fatal("original password login did not issue a session")
	}
	overviewRequest := httptest.NewRequest(http.MethodGet, "http://dashboard.test/", nil)
	overviewRequest.AddCookie(sessionCookie)
	overview := httptest.NewRecorder()
	application.Handler.ServeHTTP(overview, overviewRequest)
	if overview.Code != http.StatusOK {
		t.Fatalf("seeded account overview returned %d", overview.Code)
	}
	adminRequest := httptest.NewRequest(http.MethodGet, "http://dashboard.test/admin/users", nil)
	adminRequest.AddCookie(sessionCookie)
	admin := httptest.NewRecorder()
	application.Handler.ServeHTTP(admin, adminRequest)
	if admin.Code != http.StatusOK {
		t.Fatalf("seeded owner admin page returned %d", admin.Code)
	}
}

func TestDemoAccountSeedPreservesExistingCredentials(t *testing.T) {
	application, err := New(config.Config{Demo: true})
	if err != nil {
		t.Fatal(err)
	}
	before, ok := application.Store.FindByEmail(demoAccountEmail)
	if !ok {
		t.Fatal("original demo account is missing")
	}
	if err := seedDemoAccount(application.Store); err != nil {
		t.Fatal(err)
	}
	after, ok := application.Store.FindByEmail(demoAccountEmail)
	if !ok || after.ID != before.ID || after.User.PasswordHash != before.User.PasswordHash ||
		after.ActiveOrganizationID != before.ActiveOrganizationID || after.ActiveProjectID != before.ActiveProjectID {
		t.Fatal("second demo seed changed the existing account")
	}

	const configuredPassword = "configured-strong-password"
	configured, err := New(config.Config{
		Demo: true, AdminEmail: demoAccountEmail, AdminPassword: configuredPassword,
	})
	if err != nil {
		t.Fatal(err)
	}
	admin, ok := configured.Store.FindByEmail(demoAccountEmail)
	if !ok || !admin.User.PlatformAdmin || admin.User.Name != "Demo administrator" ||
		!auth.VerifyPassword(admin.User.PasswordHash, configuredPassword) ||
		auth.VerifyPassword(admin.User.PasswordHash, originalDemoPassword) {
		t.Fatal("demo seed overrode explicitly configured administrator")
	}
	if _, err := New(config.Config{}); err == nil {
		t.Fatal("non-demo mode unexpectedly started with a seeded account")
	}
}

func TestStaticAssetsAreCompressedCachedAndMutationsDropPrefetches(t *testing.T) {
	application, err := New(config.Config{Demo: true})
	if err != nil {
		t.Fatal(err)
	}
	serve := func(method, target string, header http.Header) *httptest.ResponseRecorder {
		request := httptest.NewRequest(method, "http://dashboard.test"+target, nil)
		for key, values := range header {
			request.Header[key] = values
		}
		response := httptest.NewRecorder()
		application.Handler.ServeHTTP(response, request)
		return response
	}
	css := serve(http.MethodGet, static.URL("css/terrascale.css"), http.Header{"Accept-Encoding": {"br, gzip"}})
	if css.Code != http.StatusOK || css.Header().Get("Content-Encoding") != "br" ||
		css.Header().Get("Cache-Control") != "public, max-age=31536000, immutable" {
		t.Fatalf("versioned stylesheet: %d %v", css.Code, css.Header())
	}
	etag := css.Header().Get("ETag")
	if revalidated := serve(http.MethodGet, "/css/terrascale.css", http.Header{"If-None-Match": {etag}}); revalidated.Code != http.StatusNotModified {
		t.Fatalf("unversioned stylesheet revalidation: %d", revalidated.Code)
	}
	if font := serve(http.MethodGet, "/fonts/oxanium-latin-wght-normal.woff2", http.Header{"Accept-Encoding": {"br"}}); font.Header().Get("Content-Encoding") != "" {
		t.Fatal("already-compressed fonts were compressed again")
	}
	if post := serve(http.MethodPost, "/api/v1/auth/logout", nil); post.Header().Get("Clear-Site-Data") != `"prefetchCache"` {
		t.Fatalf("mutation did not clear prefetched pages: %v", post.Header())
	}
	if page := serve(http.MethodGet, "/login", nil); page.Header().Get("Clear-Site-Data") != "" {
		t.Fatal("a page view cleared prefetched pages")
	}
}
