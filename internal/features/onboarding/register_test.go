package onboarding

import (
	"net/http"
	"net/http/httptest"
	"net/url"
	"regexp"
	"strings"
	"testing"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/core/memstore"
	"terrascale.tech/dashboard/internal/platform/csrf"
	renderer "terrascale.tech/dashboard/internal/platform/render"
	"terrascale.tech/dashboard/internal/platform/session"
)

func newRegistrationMux() *http.ServeMux {
	store := memstore.New()
	sessions := session.New(store, session.Options{})
	guard := csrf.New(sessions, csrf.Options{})
	mux := http.NewServeMux()
	Mount(mux, core.Deps{Store: store, Sessions: sessions, CSRF: guard, Render: renderer.New(store, guard, nil, sessions.NoticeScope, false), Tools: core.NewTools(store), Demo: true}, nil, nil)
	return mux
}

func postRegistration(mux *http.ServeMux, form url.Values) *httptest.ResponseRecorder {
	request := httptest.NewRequest(http.MethodPost, "/onboarding", strings.NewReader(form.Encode()))
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

func registration(password, confirmation string) url.Values {
	return url.Values{
		"displayName": {"Ada Lovelace"}, "email": {"ada@example.test"},
		"password": {password}, "confirmPassword": {confirmation}, "terms": {"true"},
	}
}

func TestRegistrationErrorKeepsInputAndMarksTheField(t *testing.T) {
	t.Parallel()
	response := postRegistration(newRegistrationMux(), registration("correct-horse-battery", "correct-horse-batteries"))

	body := response.Body.String()
	if response.Code != http.StatusUnprocessableEntity {
		t.Fatalf("mismatched passwords = %d, want 422", response.Code)
	}
	if !strings.Contains(inputTag(t, body, "displayName"), `value="Ada Lovelace"`) ||
		!strings.Contains(inputTag(t, body, "email"), `value="ada@example.test"`) ||
		!strings.Contains(inputTag(t, body, "terms"), "checked") {
		t.Fatal("registration error dropped the name, email or accepted terms")
	}
	if strings.Contains(body, "correct-horse-batter") {
		t.Fatal("registration error echoed a password back")
	}
	confirmation := inputTag(t, body, "confirmPassword")
	for _, want := range []string{`aria-invalid="true"`, `aria-describedby="confirmPassword-error"`, "autofocus"} {
		if !strings.Contains(confirmation, want) {
			t.Fatalf("confirmation input = %s, want %s", confirmation, want)
		}
	}
	if !strings.Contains(body, `<p class="ts-field-error" id="confirmPassword-error">Passwords do not match.</p>`) {
		t.Fatal("registration error is not shown under the confirmation field")
	}
	if password := inputTag(t, body, "password"); strings.Contains(password, "aria-invalid") ||
		!strings.Contains(password, `aria-describedby="password-hint"`) || !strings.Contains(password, `minlength="12"`) {
		t.Fatalf("password input = %s, want it valid, described by its hint and at least 12 characters", password)
	}
}

func TestRegistrationWithTakenEmailPointsToSignIn(t *testing.T) {
	t.Parallel()
	mux := newRegistrationMux()
	if response := postRegistration(mux, registration("correct-horse-battery", "correct-horse-battery")); response.Code != http.StatusSeeOther {
		t.Fatalf("first registration = %d, want 303", response.Code)
	}

	response := postRegistration(mux, registration("another-horse-battery", "another-horse-battery"))
	body := response.Body.String()
	if response.Code != http.StatusConflict || !strings.Contains(body, `id="email-error">An account with this email already exists.`) {
		t.Fatalf("taken email = %d, want 409 with the error under the email field", response.Code)
	}
	if email := inputTag(t, body, "email"); !strings.Contains(email, `aria-invalid="true"`) || !strings.Contains(email, `value="ada@example.test"`) {
		t.Fatalf("email input = %s, want it kept and marked invalid", email)
	}
	if !strings.Contains(body, `href="/login"`) {
		t.Fatal("taken email page has no way to sign in")
	}
}
