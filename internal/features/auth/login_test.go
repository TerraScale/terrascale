package auth

import (
	"errors"
	"net/http"
	"net/http/httptest"
	"net/url"
	"regexp"
	"strings"
	"testing"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/core/memstore"
	"terrascale.tech/dashboard/internal/platform/csrf"
	"terrascale.tech/dashboard/internal/platform/render"
	"terrascale.tech/dashboard/internal/platform/session"
	"terrascale.tech/dashboard/internal/platform/throttle"
)

const (
	testEmail    = "ada@example.test"
	testPassword = "correct-horse-battery"
)

// newLoginMux mounts the auth routes with one registered account, throttling
// failed sign-ins with limits (zero budgets use the defaults).
func newLoginMux(t *testing.T, limits throttle.Options) *http.ServeMux {
	t.Helper()
	store := memstore.New()
	sessions := session.New(store, session.Options{})
	guard := csrf.New(sessions, csrf.Options{})
	deps := core.Deps{
		Store: store, Sessions: sessions, CSRF: guard, Render: render.New(store, guard, nil, false),
		Throttle: throttle.New(limits),
	}
	if _, err := RegisterAccount(t.Context(), deps, RegistrationInput{
		DisplayName: "Ada Lovelace", Email: testEmail, Password: testPassword,
		ConfirmPassword: testPassword, AcceptedTerms: true,
	}); err != nil {
		t.Fatal(err)
	}
	mux := http.NewServeMux()
	Mount(mux, deps, nil)
	return mux
}

func postLogin(mux *http.ServeMux, form url.Values) *httptest.ResponseRecorder {
	request := httptest.NewRequest(http.MethodPost, "/login", strings.NewReader(form.Encode()))
	request.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	response := httptest.NewRecorder()
	mux.ServeHTTP(response, request)
	return response
}

// inputTag returns the <input> element with the given id from a rendered page.
func inputTag(t *testing.T, body, id string) string {
	t.Helper()
	for _, tag := range regexp.MustCompile(`<input[^>]*>`).FindAllString(body, -1) {
		if strings.Contains(tag, ` id="`+id+`"`) {
			return tag
		}
	}
	t.Fatalf("page has no input with id %q", id)
	return ""
}

func TestLoginFormKeepsEmailAfterRejectedCredentials(t *testing.T) {
	t.Parallel()
	mux := newLoginMux(t, throttle.Options{})
	response := postLogin(mux, url.Values{"email": {" " + testEmail + " "}, "password": {"wrong-password-123"}, "redirectTo": {"/databases"}})

	body := response.Body.String()
	if response.Code != http.StatusUnauthorized || !strings.Contains(body, "Incorrect email or password.") {
		t.Fatalf("rejected login = %d, want 401 with the credentials error", response.Code)
	}
	if email := inputTag(t, body, "email"); !strings.Contains(email, `value="`+testEmail+`"`) {
		t.Fatalf("email input = %s, want the submitted email kept", email)
	}
	if password := inputTag(t, body, "password"); !strings.Contains(password, "autofocus") || strings.Contains(password, "value=") {
		t.Fatalf("password input = %s, want it focused and empty", password)
	}
	if !strings.Contains(body, `name="redirectTo" value="/databases"`) {
		t.Fatal("rejected login dropped the return path")
	}
	for _, cookie := range response.Result().Cookies() {
		if cookie.Name == "ts_dashboard_session" {
			t.Fatal("rejected login started a session")
		}
	}
}

func TestLoginFormAsksForMissingFields(t *testing.T) {
	t.Parallel()
	mux := newLoginMux(t, throttle.Options{})
	response := postLogin(mux, url.Values{"email": {"  "}, "password": {""}})

	body := response.Body.String()
	if response.Code != http.StatusUnprocessableEntity {
		t.Fatalf("empty login = %d, want 422", response.Code)
	}
	for _, want := range []string{
		`<p class="ts-field-error" id="email-error">Enter your email address.</p>`,
		`<p class="ts-field-error" id="password-error">Enter your password.</p>`,
	} {
		if !strings.Contains(body, want) {
			t.Fatalf("empty login page is missing %s", want)
		}
	}
	email := inputTag(t, body, "email")
	for _, want := range []string{`aria-invalid="true"`, `aria-describedby="email-error"`, "autofocus"} {
		if !strings.Contains(email, want) {
			t.Fatalf("email input = %s, want %s", email, want)
		}
	}
	if password := inputTag(t, body, "password"); !strings.Contains(password, `aria-describedby="password-error"`) {
		t.Fatalf("password input = %s, want it described by its error", password)
	}
	if strings.Contains(body, "Incorrect email or password.") {
		t.Fatal("empty login reported rejected credentials")
	}
}

func TestLoginFormRedirectsAfterSignIn(t *testing.T) {
	t.Parallel()
	mux := newLoginMux(t, throttle.Options{})
	for _, tc := range []struct{ redirectTo, want string }{
		{redirectTo: "", want: "/onboarding"},
		{redirectTo: "/databases?tab=all", want: "/databases?tab=all"},
		{redirectTo: "https://evil.example/", want: "/onboarding"},
	} {
		t.Run(tc.redirectTo, func(t *testing.T) {
			t.Parallel()
			response := postLogin(mux, url.Values{"email": {testEmail}, "password": {testPassword}, "redirectTo": {tc.redirectTo}})
			if response.Code != http.StatusSeeOther || response.Header().Get("Location") != tc.want {
				t.Fatalf("login with redirectTo %q = %d to %q, want %q", tc.redirectTo, response.Code, response.Header().Get("Location"), tc.want)
			}
		})
	}
}

func TestLoginPageIgnoresErrorQuery(t *testing.T) {
	t.Parallel()
	mux := newLoginMux(t, throttle.Options{})
	response := httptest.NewRecorder()
	mux.ServeHTTP(response, httptest.NewRequest(http.MethodGet, "/login?error="+url.QueryEscape("Account locked. Call +1 555 0100."), nil))
	if response.Code != http.StatusOK || strings.Contains(response.Body.String(), "Account locked") {
		t.Fatalf("GET /login?error= = %d, want the page without the query text", response.Code)
	}
}

func TestLoginAPIAcceptsOnlyJSON(t *testing.T) {
	t.Parallel()
	mux := newLoginMux(t, throttle.Options{})
	for _, tc := range []struct {
		contentType, body string
		status            int
		want              string
	}{
		{contentType: "application/x-www-form-urlencoded", body: "email=" + testEmail + "&password=" + testPassword, status: http.StatusUnsupportedMediaType},
		{contentType: "application/json", body: `{"email":"` + testEmail + `","password":"wrong-password-123"}`, status: http.StatusUnauthorized, want: `"invalid_credentials"`},
		{contentType: "application/json", body: `{"email":"` + testEmail + `","password":"` + testPassword + `"}`, status: http.StatusOK, want: `"email":"` + testEmail + `"`},
	} {
		t.Run(http.StatusText(tc.status), func(t *testing.T) {
			t.Parallel()
			request := httptest.NewRequest(http.MethodPost, "/api/v1/auth/password/login", strings.NewReader(tc.body))
			request.Header.Set("Content-Type", tc.contentType)
			response := httptest.NewRecorder()
			mux.ServeHTTP(response, request)
			if response.Code != tc.status || !strings.Contains(response.Body.String(), tc.want) {
				t.Fatalf("%s login = %d %s, want %d containing %s", tc.contentType, response.Code, response.Body.String(), tc.status, tc.want)
			}
		})
	}
}

func TestRegisterAccountNamesTheInvalidField(t *testing.T) {
	t.Parallel()
	store := memstore.New()
	deps := core.Deps{Store: store}
	valid := RegistrationInput{DisplayName: "Ada Lovelace", Email: testEmail, Password: testPassword, ConfirmPassword: testPassword, AcceptedTerms: true}
	for _, tc := range []struct {
		name  string
		edit  func(*RegistrationInput)
		field string
	}{
		{name: "blank name", edit: func(in *RegistrationInput) { in.DisplayName = "  " }, field: "displayName"},
		{name: "control characters in name", edit: func(in *RegistrationInput) { in.DisplayName = "Ada\x00" }, field: "displayName"},
		{name: "blank email", edit: func(in *RegistrationInput) { in.Email = "" }, field: "email"},
		{name: "email without domain dot", edit: func(in *RegistrationInput) { in.Email = "ada@example" }, field: "email"},
		{name: "blank password", edit: func(in *RegistrationInput) { in.Password, in.ConfirmPassword = "", "" }, field: "password"},
		{name: "short password", edit: func(in *RegistrationInput) { in.Password, in.ConfirmPassword = "short", "short" }, field: "password"},
		{name: "blank confirmation", edit: func(in *RegistrationInput) { in.ConfirmPassword = "" }, field: "confirmPassword"},
		{name: "mismatched confirmation", edit: func(in *RegistrationInput) { in.ConfirmPassword = testPassword + "!" }, field: "confirmPassword"},
		{name: "terms not accepted", edit: func(in *RegistrationInput) { in.AcceptedTerms = false }, field: "terms"},
	} {
		t.Run(tc.name, func(t *testing.T) {
			t.Parallel()
			input := valid
			tc.edit(&input)
			_, err := RegisterAccount(t.Context(), deps, input)
			inputError, ok := errors.AsType[InputError](err)
			if !ok || inputError.Field != tc.field || inputError.Message == "" {
				t.Fatalf("RegisterAccount error = %#v, want an InputError for %q", err, tc.field)
			}
		})
	}
}

func TestLoginPageSignUpLinkKeepsTheReturnPath(t *testing.T) {
	t.Parallel()
	mux := newLoginMux(t, throttle.Options{})
	for _, tc := range []struct{ path, want string }{
		{path: "/login?returnTo=%2Finvitations%2Ftok%3Fx%3D1", want: `href="/onboarding?returnTo=%2Finvitations%2Ftok%3Fx%3D1"`},
		{path: "/login", want: `href="/onboarding"`},
		{path: "/login?returnTo=https%3A%2F%2Fevil.example%2F", want: `href="/onboarding"`},
	} {
		response := httptest.NewRecorder()
		mux.ServeHTTP(response, httptest.NewRequest(http.MethodGet, tc.path, nil))
		if !strings.Contains(response.Body.String(), tc.want+`>Create account</a>`) {
			t.Fatalf("GET %s has no Create account link with %s", tc.path, tc.want)
		}
	}
}
