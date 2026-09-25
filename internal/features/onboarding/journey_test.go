package onboarding_test

import (
	"bytes"
	"encoding/json/v2"
	"io"
	"net/http"
	"net/http/cookiejar"
	"net/http/httptest"
	"net/url"
	"regexp"
	"strconv"
	"strings"
	"testing"
	"time"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/features/auth"
	"terrascale.tech/dashboard/internal/features/onboarding"
	"terrascale.tech/dashboard/internal/features/profile"
	"terrascale.tech/dashboard/internal/features/subscriptions"
	"terrascale.tech/dashboard/internal/platform/csrf"
	"terrascale.tech/dashboard/internal/platform/render"
	"terrascale.tech/dashboard/internal/platform/session"
)

type journey struct {
	server   *httptest.Server
	store    *core.Store
	sessions *session.Manager
}

func newJourney(t *testing.T) journey {
	t.Helper()
	store := core.NewStore()
	sessions := session.New(store, session.Options{CookieName: "ts_session", Lifetime: time.Hour})
	guard := csrf.New(sessions, csrf.Options{CookieName: "ts_csrf"})
	renderer := render.New(store, guard)
	deps := core.Deps{Store: store, Sessions: sessions, CSRF: guard, Render: renderer, Demo: true}
	mux := http.NewServeMux()
	auth.Mount(mux, deps)
	onboarding.Mount(mux, deps)
	profile.Mount(mux, deps)
	subscriptions.Mount(mux, deps)
	server := httptest.NewServer(guard.Protect(mux))
	t.Cleanup(server.Close)
	return journey{server: server, store: store, sessions: sessions}
}

func newClient(t *testing.T, server *httptest.Server) *http.Client {
	t.Helper()
	jar, err := cookiejar.New(nil)
	if err != nil {
		t.Fatalf("cookiejar.New() error = %v", err)
	}
	return &http.Client{
		Jar:           jar,
		CheckRedirect: func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse },
	}
}

var csrfTokenPattern = regexp.MustCompile(`name="_csrf" value="([^"]+)"`)

func csrfToken(t *testing.T, client *http.Client, server *httptest.Server, path string) string {
	t.Helper()
	response, err := client.Get(server.URL + path)
	if err != nil {
		t.Fatalf("GET %s error = %v", path, err)
	}
	defer response.Body.Close()
	if response.StatusCode != http.StatusOK {
		t.Fatalf("GET %s status = %d, want %d", path, response.StatusCode, http.StatusOK)
	}
	body, err := io.ReadAll(response.Body)
	if err != nil {
		t.Fatalf("read GET %s body: %v", path, err)
	}
	match := csrfTokenPattern.FindStringSubmatch(string(body))
	if len(match) != 2 {
		t.Fatalf("GET %s did not render a CSRF token: %s", path, body)
	}
	return match[1]
}

func postForm(t *testing.T, client *http.Client, server *httptest.Server, path string, values url.Values) *http.Response {
	t.Helper()
	values = cloneValues(values)
	values.Set("_csrf", csrfToken(t, client, server, formPage(path)))
	response, err := client.PostForm(server.URL+path, values)
	if err != nil {
		t.Fatalf("POST %s error = %v", path, err)
	}
	return response
}

func postJSON(t *testing.T, client *http.Client, server *httptest.Server, path string, value any) *http.Response {
	t.Helper()
	body, err := json.Marshal(value)
	if err != nil {
		t.Fatalf("json.Marshal() error = %v", err)
	}
	request, err := http.NewRequest(http.MethodPost, server.URL+path, bytes.NewReader(body))
	if err != nil {
		t.Fatalf("NewRequest() error = %v", err)
	}
	request.Header.Set("Content-Type", "application/json; charset=utf-8")
	request.Header.Set("X-CSRF-Token", csrfToken(t, client, server, formPage(path)))
	request.Header.Set("Origin", server.URL)
	response, err := client.Do(request)
	if err != nil {
		t.Fatalf("POST %s JSON error = %v", path, err)
	}
	return response
}

func formPage(path string) string {
	switch path {
	case "/onboarding/register", "/onboarding/step":
		return "/onboarding"
	case "/api/v1/auth/password/login":
		return "/login"
	case "/api/v1/dashboard/profile", "/api/v1/dashboard/profile/password":
		return "/profile"
	case "/api/v1/auth/logout":
		return "/profile"
	default:
		return "/"
	}
}

func cloneValues(values url.Values) url.Values {
	clone := make(url.Values, len(values))
	for key, entries := range values {
		clone[key] = append([]string(nil), entries...)
	}
	return clone
}

func registerAccount(t *testing.T, client *http.Client, server *httptest.Server, name, email, password string) {
	t.Helper()
	response := postForm(t, client, server, "/onboarding/register", url.Values{
		"displayName":     {name},
		"email":           {email},
		"password":        {password},
		"confirmPassword": {password},
		"terms":           {"true"},
	})
	defer response.Body.Close()
	if response.StatusCode != http.StatusSeeOther || response.Header.Get("Location") != "/onboarding" {
		t.Fatalf("register status/location = %d %q, want redirect to /onboarding", response.StatusCode, response.Header.Get("Location"))
	}
}

func assertOnboardingProgress(t *testing.T, client *http.Client, server *httptest.Server, currentStep, percent int) {
	t.Helper()
	response, err := client.Get(server.URL + "/onboarding")
	if err != nil {
		t.Fatalf("GET /onboarding error = %v", err)
	}
	defer response.Body.Close()
	body, err := io.ReadAll(response.Body)
	if err != nil {
		t.Fatalf("read onboarding page: %v", err)
	}
	content := string(body)
	stepLabel := "Step " + strconv.Itoa(currentStep) + " of 3"
	progress := strconv.Itoa(percent) + "%"
	ariaValue := `aria-valuenow="` + strconv.Itoa(percent) + `"`
	progressData := `data-progress="` + strconv.Itoa(percent) + `"`
	if response.StatusCode != http.StatusOK || !strings.Contains(content, stepLabel) ||
		!strings.Contains(content, progress) || !strings.Contains(content, ariaValue) ||
		!strings.Contains(content, progressData) || strings.Contains(content, `style="width:`) {
		t.Fatalf("onboarding progress is %d/%s at status %d: %s", currentStep, progress, response.StatusCode, content)
	}
}

type meResponse struct {
	User struct {
		ID                  string `json:"id"`
		Email               string `json:"email"`
		Name                string `json:"name"`
		Phone               string `json:"phone"`
		OnboardingCompleted bool   `json:"onboardingCompleted"`
	} `json:"user"`
}

func getMe(t *testing.T, client *http.Client, server *httptest.Server) (int, meResponse) {
	t.Helper()
	response, err := client.Get(server.URL + "/api/v1/auth/me")
	if err != nil {
		t.Fatalf("GET /api/v1/auth/me error = %v", err)
	}
	defer response.Body.Close()
	var result meResponse
	if response.StatusCode == http.StatusOK {
		if err := json.UnmarshalRead(response.Body, &result); err != nil {
			t.Fatalf("decode /api/v1/auth/me: %v", err)
		}
	}
	return response.StatusCode, result
}

func TestRegistrationOnboardingLoginAndProfileStayAccountScoped(t *testing.T) {
	app := newJourney(t)
	firstClient := newClient(t, app.server)
	secondClient := newClient(t, app.server)
	const firstPassword = "First-account-password-123!"
	const secondPassword = "Second-account-password-456!"
	assertOnboardingProgress(t, firstClient, app.server, 1, 33)
	registerAccount(t, firstClient, app.server, "First User", "First@example.test", firstPassword)
	assertOnboardingProgress(t, firstClient, app.server, 2, 66)
	firstAccount, found := app.store.FindByEmail(" FIRST@example.test ")
	if !found {
		t.Fatal("new registration was not stored under normalized email")
	}
	if firstAccount.User.PasswordHash == firstPassword || !auth.VerifyPassword(firstAccount.User.PasswordHash, firstPassword) {
		t.Fatal("registration did not persist only a valid password hash")
	}
	if firstAccount.ActiveOrganizationID == "" || firstAccount.ActiveProjectID == "" {
		t.Fatal("registration did not create an active Personal workspace and project")
	}

	for index, step := range []url.Values{
		{"step": {"payment-method"}, "bypass": {"1"}},
		{"step": {"project"}, "name": {"First Project"}, "region": {"GRU"}},
	} {
		response := postForm(t, firstClient, app.server, "/onboarding/step", step)
		response.Body.Close()
		if response.StatusCode != http.StatusSeeOther {
			t.Fatalf("onboarding step status = %d, want redirect", response.StatusCode)
		}
		if index == 0 {
			assertOnboardingProgress(t, firstClient, app.server, 3, 100)
		}
	}
	firstAccount, _ = app.store.FindByEmail("first@example.test")
	if !firstAccount.OnboardingCompleted {
		t.Fatal("project step did not complete onboarding")
	}
	project := firstAccount.Organizations[firstAccount.ActiveOrganizationID].Projects[firstAccount.ActiveProjectID]
	if project.Name != "First Project" || project.Region != "GRU" {
		t.Fatalf("project = %#v, want renamed project in GRU", project)
	}

	registerAccount(t, secondClient, app.server, "Second User", "second@example.test", secondPassword)
	if status, me := getMe(t, firstClient, app.server); status != http.StatusOK || me.User.Email != "first@example.test" {
		t.Fatalf("first account session returned status=%d email=%q", status, me.User.Email)
	}
	if status, me := getMe(t, secondClient, app.server); status != http.StatusOK || me.User.Email != "second@example.test" {
		t.Fatalf("second account session returned status=%d email=%q", status, me.User.Email)
	}

	response := postForm(t, firstClient, app.server, "/api/v1/dashboard/profile", url.Values{
		"name":        {"Renamed First User"},
		"phoneNumber": {"+55 11 99999-0001"},
	})
	response.Body.Close()
	if response.StatusCode != http.StatusSeeOther || response.Header.Get("Location") != "/profile" {
		t.Fatalf("profile update status/location = %d %q", response.StatusCode, response.Header.Get("Location"))
	}
	if status, me := getMe(t, firstClient, app.server); status != http.StatusOK || me.User.Name != "Renamed First User" || me.User.Phone != "+55 11 99999-0001" {
		t.Fatalf("updated profile = status %d, %#v", status, me.User)
	}
	if status, me := getMe(t, secondClient, app.server); status != http.StatusOK || me.User.Name != "Second User" || me.User.Phone != "" {
		t.Fatalf("profile mutation crossed accounts: status %d, %#v", status, me.User)
	}

	duplicateClient := newClient(t, app.server)
	duplicate := postForm(t, duplicateClient, app.server, "/onboarding/register", url.Values{
		"displayName":     {"Duplicate"},
		"email":           {"FIRST@EXAMPLE.TEST"},
		"password":        {"Another-account-password-123!"},
		"confirmPassword": {"Another-account-password-123!"},
		"terms":           {"true"},
	})
	duplicate.Body.Close()
	if duplicate.StatusCode != http.StatusSeeOther || !strings.HasPrefix(duplicate.Header.Get("Location"), "/onboarding?error=") {
		t.Fatalf("duplicate registration status/location = %d %q", duplicate.StatusCode, duplicate.Header.Get("Location"))
	}

	loginClient := newClient(t, app.server)
	wrong := postForm(t, loginClient, app.server, "/api/v1/auth/password/login", url.Values{
		"email":    {"first@example.test"},
		"password": {"wrong-password"},
	})
	wrong.Body.Close()
	if wrong.StatusCode != http.StatusSeeOther || !strings.HasPrefix(wrong.Header.Get("Location"), "/login?error=") {
		t.Fatalf("wrong credential status/location = %d %q", wrong.StatusCode, wrong.Header.Get("Location"))
	}
	correct := postForm(t, loginClient, app.server, "/api/v1/auth/password/login", url.Values{
		"email":      {"first@example.test"},
		"password":   {firstPassword},
		"redirectTo": {"//attacker.test/path"},
	})
	correct.Body.Close()
	if correct.StatusCode != http.StatusSeeOther || correct.Header.Get("Location") != "/" {
		t.Fatalf("login redirect status/location = %d %q", correct.StatusCode, correct.Header.Get("Location"))
	}
	if status, me := getMe(t, loginClient, app.server); status != http.StatusOK || me.User.Email != "first@example.test" {
		t.Fatalf("successful login returned status=%d email=%q", status, me.User.Email)
	}

	jsonClient := newClient(t, app.server)
	wrongJSON := postJSON(t, jsonClient, app.server, "/api/v1/auth/password/login", map[string]string{
		"email": "first@example.test", "password": "wrong-password",
	})
	wrongJSONBody, err := io.ReadAll(wrongJSON.Body)
	wrongJSON.Body.Close()
	if err != nil || wrongJSON.StatusCode != http.StatusUnauthorized || wrongJSON.Header.Get("Content-Type") != "application/json; charset=utf-8" || string(wrongJSONBody) != `{"error":"invalid_credentials"}` {
		t.Fatalf("wrong JSON login status/content-type = %d %q", wrongJSON.StatusCode, wrongJSON.Header.Get("Content-Type"))
	}
	unknownJSON := postJSON(t, jsonClient, app.server, "/api/v1/auth/password/login", map[string]string{
		"email": "unknown@example.test", "password": "wrong-password",
	})
	unknownJSONBody, err := io.ReadAll(unknownJSON.Body)
	unknownJSON.Body.Close()
	if err != nil || unknownJSON.StatusCode != http.StatusUnauthorized || string(unknownJSONBody) != string(wrongJSONBody) {
		t.Fatalf("unknown account credential response = %d %q, want same generic response", unknownJSON.StatusCode, unknownJSONBody)
	}
	correctJSON := postJSON(t, jsonClient, app.server, "/api/v1/auth/password/login", map[string]string{
		"email": "first@example.test", "password": firstPassword,
	})
	correctJSON.Body.Close()
	if correctJSON.StatusCode != http.StatusOK {
		t.Fatalf("correct JSON login status = %d, want 200", correctJSON.StatusCode)
	}
	if status, me := getMe(t, jsonClient, app.server); status != http.StatusOK || me.User.Email != "first@example.test" {
		t.Fatalf("successful JSON login returned status=%d email=%q", status, me.User.Email)
	}

	logout := postForm(t, firstClient, app.server, "/api/v1/auth/logout", url.Values{})
	logout.Body.Close()
	if logout.StatusCode != http.StatusSeeOther || logout.Header.Get("Location") != "/login" {
		t.Fatalf("logout status/location = %d %q", logout.StatusCode, logout.Header.Get("Location"))
	}
	if status, _ := getMe(t, firstClient, app.server); status != http.StatusUnauthorized {
		t.Fatalf("logged-out session /me status = %d, want 401", status)
	}
}

func TestRegistrationValidatesEmailPasswordConfirmationAndTerms(t *testing.T) {
	app := newJourney(t)
	cases := []struct {
		name            string
		email           string
		password        string
		confirmPassword string
		terms           string
		wantError       string
	}{
		{name: "Email User", email: "not-an-email", password: "Registration-password-123!", confirmPassword: "Registration-password-123!", terms: "true", wantError: "Enter a valid email address."},
		{name: "Short Password", email: "short@example.test", password: "short", confirmPassword: "short", terms: "true", wantError: "Use at least 12 characters."},
		{name: "Mismatch User", email: "mismatch@example.test", password: "Registration-password-123!", confirmPassword: "different-password", terms: "true", wantError: "Passwords do not match."},
		{name: "Terms User", email: "terms@example.test", password: "Registration-password-123!", confirmPassword: "Registration-password-123!", wantError: "You must accept the terms to continue."},
	}
	for _, testCase := range cases {
		client := newClient(t, app.server)
		response := postForm(t, client, app.server, "/onboarding/register", url.Values{
			"displayName":     {testCase.name},
			"email":           {testCase.email},
			"password":        {testCase.password},
			"confirmPassword": {testCase.confirmPassword},
			"terms":           {testCase.terms},
		})
		response.Body.Close()
		location, err := url.Parse(response.Header.Get("Location"))
		if response.StatusCode != http.StatusSeeOther || err != nil || location.Query().Get("error") != testCase.wantError {
			t.Errorf("%s registration status/location = %d %q, want error %q", testCase.name, response.StatusCode, response.Header.Get("Location"), testCase.wantError)
		}
		if _, found := app.store.FindByEmail(testCase.email); found {
			t.Errorf("%s invalid registration created an account", testCase.name)
		}
	}
}

func TestPasswordChangeRevokesSessionsAndCSRFProtectsCookieFlows(t *testing.T) {
	app := newJourney(t)
	firstClient := newClient(t, app.server)
	registerAccount(t, firstClient, app.server, "Password User", "password@example.test", "Before-change-password-123!")
	secondClient := newClient(t, app.server)
	login := postForm(t, secondClient, app.server, "/api/v1/auth/password/login", url.Values{
		"email":    {"password@example.test"},
		"password": {"Before-change-password-123!"},
	})
	login.Body.Close()
	if login.StatusCode != http.StatusSeeOther {
		t.Fatalf("second session login status = %d, want redirect", login.StatusCode)
	}

	missingToken, err := secondClient.PostForm(app.server.URL+"/api/v1/auth/logout", url.Values{})
	if err != nil {
		t.Fatalf("POST logout without CSRF token error = %v", err)
	}
	missingToken.Body.Close()
	if missingToken.StatusCode != http.StatusForbidden {
		t.Fatalf("POST logout without CSRF status = %d, want 403", missingToken.StatusCode)
	}

	change := postForm(t, firstClient, app.server, "/api/v1/dashboard/profile/password", url.Values{
		"currentPassword": {"Before-change-password-123!"},
		"newPassword":     {"After-change-password-456!"},
		"confirmPassword": {"After-change-password-456!"},
	})
	change.Body.Close()
	if change.StatusCode != http.StatusSeeOther || change.Header.Get("Location") != "/login" {
		t.Fatalf("password change status/location = %d %q", change.StatusCode, change.Header.Get("Location"))
	}
	if status, _ := getMe(t, firstClient, app.server); status != http.StatusUnauthorized {
		t.Fatalf("password changer session /me status = %d, want 401", status)
	}
	if status, _ := getMe(t, secondClient, app.server); status != http.StatusUnauthorized {
		t.Fatalf("other revoked session /me status = %d, want 401", status)
	}

	oldPasswordClient := newClient(t, app.server)
	oldPassword := postForm(t, oldPasswordClient, app.server, "/api/v1/auth/password/login", url.Values{
		"email":    {"password@example.test"},
		"password": {"Before-change-password-123!"},
	})
	oldPassword.Body.Close()
	if oldPassword.StatusCode != http.StatusSeeOther || !strings.HasPrefix(oldPassword.Header.Get("Location"), "/login?error=") {
		t.Fatalf("old password login status/location = %d %q", oldPassword.StatusCode, oldPassword.Header.Get("Location"))
	}
	newPasswordClient := newClient(t, app.server)
	newPassword := postForm(t, newPasswordClient, app.server, "/api/v1/auth/password/login", url.Values{
		"email":    {"password@example.test"},
		"password": {"After-change-password-456!"},
	})
	newPassword.Body.Close()
	if newPassword.StatusCode != http.StatusSeeOther || newPassword.Header.Get("Location") != "/onboarding" {
		t.Fatalf("new password login status/location = %d %q", newPassword.StatusCode, newPassword.Header.Get("Location"))
	}
	assertOnboardingProgress(t, newPasswordClient, app.server, 2, 66)
}

func TestProfileRejectsAnonymousAccess(t *testing.T) {
	app := newJourney(t)
	client := newClient(t, app.server)
	response, err := client.Get(app.server.URL + "/profile")
	if err != nil {
		t.Fatalf("GET /profile error = %v", err)
	}
	defer response.Body.Close()
	if response.StatusCode != http.StatusSeeOther || response.Header.Get("Location") != "/login" {
		t.Fatalf("anonymous profile status/location = %d %q", response.StatusCode, response.Header.Get("Location"))
	}
}

func TestPersonalPaymentCheckoutReturnsToVerifiedOnboardingStep(t *testing.T) {
	for _, outcome := range []string{"complete", "cancel"} {
		t.Run(outcome, func(t *testing.T) {
			app := newJourney(t)
			client := newClient(t, app.server)
			registerAccount(t, client, app.server, "Checkout User", outcome+"@example.test", "Checkout-password-123!")

			forged, err := client.Get(app.server.URL + "/onboarding?payment=success")
			if err != nil {
				t.Fatal(err)
			}
			forged.Body.Close()
			if forged.StatusCode != http.StatusOK {
				t.Fatalf("forged checkout return status = %d, want 200", forged.StatusCode)
			}
			account, _ := app.store.FindByEmail(outcome + "@example.test")
			if account.OnboardingStep != "payment-method" {
				t.Fatalf("forged success advanced onboarding to %q", account.OnboardingStep)
			}

			invalid := postForm(t, client, app.server, "/onboarding/step", url.Values{
				"step": {"payment-method"}, "plan": {"unknown"}, "interval": {"monthly"},
			})
			invalid.Body.Close()
			if invalid.StatusCode != http.StatusSeeOther || !strings.HasPrefix(invalid.Header.Get("Location"), "/onboarding?error=") {
				t.Fatalf("invalid plan status/location = %d %q", invalid.StatusCode, invalid.Header.Get("Location"))
			}

			start := postForm(t, client, app.server, "/onboarding/step", url.Values{
				"step": {"payment-method"}, "plan": {"pro"}, "interval": {"annual"},
			})
			start.Body.Close()
			checkoutPath := start.Header.Get("Location")
			if start.StatusCode != http.StatusSeeOther || !strings.HasPrefix(checkoutPath, "/checkout/cs_local_") {
				t.Fatalf("paid setup status/location = %d %q", start.StatusCode, checkoutPath)
			}
			checkout, err := client.Get(app.server.URL + checkoutPath)
			if err != nil {
				t.Fatal(err)
			}
			checkout.Body.Close()
			if checkout.StatusCode != http.StatusOK {
				t.Fatalf("GET checkout status = %d, want 200", checkout.StatusCode)
			}

			finishResponse, err := client.PostForm(app.server.URL+checkoutPath+"/"+outcome, url.Values{
				"_csrf": {csrfToken(t, client, app.server, checkoutPath)},
			})
			if err != nil {
				t.Fatal(err)
			}
			finishResponse.Body.Close()
			returnPath := finishResponse.Header.Get("Location")
			if finishResponse.StatusCode != http.StatusSeeOther || returnPath != "/onboarding?payment="+map[string]string{"complete": "success", "cancel": "canceled"}[outcome] {
				t.Fatalf("checkout result status/location = %d %q", finishResponse.StatusCode, returnPath)
			}

			returned, err := client.Get(app.server.URL + returnPath)
			if err != nil {
				t.Fatal(err)
			}
			returned.Body.Close()
			if returned.StatusCode != http.StatusOK {
				t.Fatalf("checkout return status = %d, want 200", returned.StatusCode)
			}
			account, _ = app.store.FindByEmail(outcome + "@example.test")
			organization := account.Organizations[account.ActiveOrganizationID]
			if outcome == "complete" {
				if account.OnboardingStep != "project" || !organization.PaymentMethodValidated || organization.PlanID != "plan_pro" {
					t.Fatalf("successful checkout state = step %q, paid %v, plan %q", account.OnboardingStep, organization.PaymentMethodValidated, organization.PlanID)
				}
			} else if account.OnboardingStep != "payment-method" || organization.PaymentMethodValidated {
				t.Fatalf("canceled checkout state = step %q, paid %v", account.OnboardingStep, organization.PaymentMethodValidated)
			}
		})
	}
}
