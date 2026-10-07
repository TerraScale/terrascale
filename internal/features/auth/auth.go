package auth

import (
	"context"
	"encoding/json/v2"
	"errors"
	"fmt"
	"math"
	"mime"
	"net/http"
	"net/mail"
	"net/url"
	"strconv"
	"strings"
	"sync"
	"time"
	"unicode"
	"unicode/utf8"

	"github.com/a-h/templ"
	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/ui"
)

const maxLoginBody = 1 << 20

type RegistrationInput struct {
	DisplayName     string
	Email           string
	Password        string
	ConfirmPassword string
	AcceptedTerms   bool
}

type apiError struct {
	Error string `json:"error"`
}

// loginPageData is the sign-in form. Error and Notice are shown above the
// form; the field errors sit under the email and password controls. TSAuth
// offers sign-in with an emailed code and single sign-on, which PromptLogin
// makes ask ts-auth for credentials again.
type loginPageData struct {
	Email         string
	RedirectTo    string
	Error         string
	Notice        string
	EmailError    string
	PasswordError string
	TSAuth        bool
	PromptLogin   bool
}

type authStatusPageData struct {
	// Status is the response status code; zero means 200 OK.
	Status        int
	Title         string
	Description   string
	Message       string
	ShowEmailForm bool
	Flow          bool
	// Recovery offers the ways to sign in after a failed one, and Reference
	// names the log line that says why it failed.
	Recovery  bool
	Reference string
	// ReturnTo is the page the failed sign-in was for.
	ReturnTo string
}

type loginUser struct {
	ID                  string `json:"id"`
	Email               string `json:"email"`
	Name                string `json:"name"`
	OnboardingCompleted bool   `json:"onboardingCompleted"`
}

type loginResponse struct {
	User loginUser `json:"user"`
}

type currentUserData struct {
	ID                  string `json:"id"`
	Email               string `json:"email"`
	Name                string `json:"name"`
	Phone               string `json:"phone"`
	Locale              string `json:"locale"`
	OnboardingCompleted bool   `json:"onboardingCompleted"`
}

type currentUserResponse struct {
	User currentUserData `json:"user"`
}

// InputError is registration input that failed validation. Field names the
// form control the message belongs to.
type InputError struct {
	Field   string
	Message string
}

func (e InputError) Error() string { return e.Message }

// errInvalidCredentials hides whether the email or the password was wrong.
var errInvalidCredentials = errors.New("invalid credentials")

var dummyPasswordHash = sync.OnceValue(func() string {
	hash, err := HashPassword("dashboard-invalid-login-dummy-password")
	if err != nil {
		panic(err)
	}
	return hash
})

// Mount registers sign-in and sign-out. With tsAuth, ts-auth holds every
// credential: the dashboard's pages check passwords and emailed codes through
// its headless API, and single sign-on redirects to it. nil keeps the
// dashboard's own password sign-in.
func Mount(mux *http.ServeMux, deps core.Deps, tsAuth *TSAuth) {
	mux.HandleFunc("GET /login", loginPage(deps, tsAuth))
	if tsAuth != nil {
		tsAuth.mountNative(mux, deps)
	} else {
		_ = dummyPasswordHash()
		mux.HandleFunc("POST /login", loginForm(deps))
		mux.HandleFunc("POST /api/v1/auth/password/login", loginAPI(deps))
		mux.HandleFunc("GET /reset-password", func(w http.ResponseWriter, r *http.Request) {
			renderAuthStatus(w, r, deps, authStatusPageData{
				Title:         "Reset password",
				Description:   demoText(deps, "Password recovery is unavailable in the in-memory demo.", "Password recovery is unavailable."),
				Message:       demoText(deps, "This demo does not send recovery emails or accept password reset tokens. Submitting this form will not send an email.", "The dashboard does not send recovery emails or accept password reset tokens. Submitting this form will not send an email."),
				ShowEmailForm: true,
			})
		})
		mux.HandleFunc("POST /reset-password", func(w http.ResponseWriter, r *http.Request) {
			renderAuthStatus(w, r, deps, authStatusPageData{
				Title:         "Reset password",
				Description:   demoText(deps, "Password recovery is unavailable in the in-memory demo.", "Password recovery is unavailable."),
				Message:       demoText(deps, "No recovery request was processed. This demo does not send recovery emails or accept password reset tokens; no password was changed.", "No recovery request was processed. The dashboard does not send recovery emails or accept password reset tokens; no password was changed."),
				ShowEmailForm: true,
			})
		})
	}
	mux.HandleFunc("GET /agent/approve", func(w http.ResponseWriter, r *http.Request) {
		renderAuthStatus(w, r, deps, authStatusPageData{
			Title:       "Approve agent access",
			Description: demoText(deps, "This in-memory demo cannot verify agent approval requests.", "The dashboard cannot verify agent approval requests yet."),
			Message:     "No verifiable agent request is available, and no access was granted.",
			Flow:        true,
		})
	})
	mux.HandleFunc("POST /agent/approve/submit", func(w http.ResponseWriter, r *http.Request) {
		renderAuthStatus(w, r, deps, authStatusPageData{
			Title:       "Approve agent access",
			Description: demoText(deps, "This in-memory demo cannot verify agent approval requests.", "The dashboard cannot verify agent approval requests yet."),
			Message:     "The approval was rejected because no verifiable agent request is available. No access was granted.",
			Flow:        true,
		})
	})
	mux.HandleFunc("GET /oauth/consent", oauthConsent(deps))
	mux.HandleFunc("POST /oauth/consent/submit", func(w http.ResponseWriter, r *http.Request) {
		renderAuthStatus(w, r, deps, authStatusPageData{
			Title:       "Authorize application",
			Description: demoText(deps, "This in-memory demo cannot verify OAuth authorization requests.", "The dashboard cannot verify OAuth authorization requests yet."),
			Message:     "The authorization request was rejected. No access was granted.",
			Flow:        true,
		})
	})
	mux.HandleFunc("POST /api/v1/auth/logout", logout(deps, tsAuth))
	mux.HandleFunc("GET /api/v1/auth/me", currentUser(deps))
}

func loginPage(deps core.Deps, tsAuth *TSAuth) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		if principal, ok := currentPrincipal(deps.Sessions, r); ok {
			// A session whose account cannot be read signs in again
			// rather than failing the login page.
			if account, err := deps.Store.SnapshotAs(r.Context(), principal); err == nil {
				location := "/"
				if !account.OnboardingCompleted {
					location = "/onboarding"
				}
				http.Redirect(w, r, location, http.StatusSeeOther)
				return
			}
		}
		returnTo := r.URL.Query().Get("returnTo")
		if returnTo == "" {
			returnTo = r.URL.Query().Get("next")
		}
		data := loginPageData{RedirectTo: SafeRedirectTarget(returnTo)}
		if tsAuth != nil && !tsAuth.Headless(r.Context()) {
			// ts-auth's own page signs in until it offers the headless API.
			if err := tsAuth.signIn(w, r, data.RedirectTo); err != nil {
				signInFailed(w, r, deps, http.StatusServiceUnavailable, "ts-auth is unavailable right now. Try again in a moment.", data.RedirectTo, err)
			}
			return
		}
		if tsAuth != nil {
			// Signing out without ts-auth's end-session endpoint asks it for
			// credentials again at the next single sign-on.
			data.TSAuth, data.PromptLogin = true, r.URL.Query().Get("prompt") == "login"
			if r.URL.Query().Get("notice") == passwordResetNotice {
				data.Notice = "Your password was changed. Sign in with your new password."
				data.Email = r.URL.Query().Get("email")
			}
		}
		renderLogin(w, r, deps, http.StatusOK, data)
	}
}

// loginForm signs in from the browser form. A failed attempt renders the form
// again with the email kept, so the visitor only retypes the password.
func loginForm(deps core.Deps) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		r.Body = http.MaxBytesReader(w, r.Body, maxLoginBody)
		if err := r.ParseForm(); err != nil {
			http.Error(w, "invalid login form", http.StatusBadRequest)
			return
		}
		data := loginPageData{
			Email:      strings.TrimSpace(r.PostForm.Get("email")),
			RedirectTo: SafeRedirectTarget(r.PostForm.Get("redirectTo")),
		}
		password := r.PostForm.Get("password")
		if data.Email == "" {
			data.EmailError = "Enter your email address."
		}
		if password == "" {
			data.PasswordError = "Enter your password."
		}
		if data.EmailError != "" || data.PasswordError != "" {
			renderLogin(w, r, deps, http.StatusUnprocessableEntity, data)
			return
		}
		account, err := startPasswordSession(w, r, deps, data.Email, password)
		if errors.Is(err, errInvalidCredentials) {
			data.Error = "Incorrect email or password."
			renderLogin(w, r, deps, http.StatusUnauthorized, data)
			return
		}
		if refused, ok := errors.AsType[tooManyAttempts](err); ok {
			data.Error = refused.retryAfter(w)
			renderLogin(w, r, deps, http.StatusTooManyRequests, data)
			return
		}
		if err != nil {
			http.Error(w, "could not create session", http.StatusInternalServerError)
			return
		}
		location := data.RedirectTo
		if location == "/" && !account.OnboardingCompleted {
			location = "/onboarding"
		}
		http.Redirect(w, r, location, http.StatusSeeOther)
	}
}

// passwordAttrs links the password control to its error and focuses it after
// rejected credentials, when only the password needs retyping.
func passwordAttrs(data loginPageData) templ.Attributes {
	attrs := ui.FieldProps{ID: "password", Error: data.PasswordError}.ControlAttrs()
	if data.Error != "" {
		attrs["autofocus"] = true
	}
	return attrs
}

func renderLogin(w http.ResponseWriter, r *http.Request, deps core.Deps, status int, data loginPageData) {
	page := core.Page{
		Layout: "auth",
		Title:  "Sign in",
		Status: status,
		View: func(p core.Page) templ.Component {
			return loginView(data, p.CSRFToken)
		},
	}
	if err := deps.Render.Page(w, r, page); err != nil {
		http.Error(w, "could not render login page", http.StatusInternalServerError)
	}
}

func oauthConsent(deps core.Deps) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		if deps.Sessions == nil {
			redirectToLogin(w, r)
			return
		}
		principal, authenticated := currentPrincipal(deps.Sessions, r)
		if !authenticated || deps.Store == nil {
			redirectToLogin(w, r)
			return
		}
		if err := core.Authorize(r.Context(), deps.Store, principal); err != nil {
			redirectToLogin(w, r)
			return
		}
		renderAuthStatus(w, r, deps, authStatusPageData{
			Title:       "Authorize application",
			Description: demoText(deps, "This in-memory demo cannot verify OAuth authorization requests.", "The dashboard cannot verify OAuth authorization requests yet."),
			Message:     "The authorization request is missing or cannot be verified. No access was granted.",
			Flow:        true,
		})
	}
}

// demoText names the in-memory demo only where it runs.
func demoText(deps core.Deps, demo, production string) string {
	if deps.Demo {
		return demo
	}
	return production
}

func redirectToLogin(w http.ResponseWriter, r *http.Request) {
	returnTo := SafeRedirectTarget(r.URL.RequestURI())
	if returnTo == "/" {
		returnTo = "/oauth/consent"
	}
	http.Redirect(w, r, "/login?returnTo="+url.QueryEscape(returnTo), http.StatusSeeOther)
}

func renderAuthStatus(w http.ResponseWriter, r *http.Request, deps core.Deps, data authStatusPageData) {
	page := core.Page{
		Layout: "auth",
		Title:  data.Title,
		Status: data.Status,
		View: func(p core.Page) templ.Component {
			return authStatusView(data, p.CSRFToken)
		},
	}
	if err := deps.Render.Page(w, r, page); err != nil {
		http.Error(w, "could not render authentication status page", http.StatusInternalServerError)
	}
}

// RegisterAccount validates and creates one independently authenticated account.
// The store enforces email uniqueness again while committing, so concurrent
// requests cannot create duplicate accounts after this early duplicate check.
func RegisterAccount(ctx context.Context, deps core.Deps, input RegistrationInput) (core.Account, error) {
	name, email, err := ValidateRegistration(input)
	if err != nil {
		return core.Account{}, err
	}
	if _, err := deps.Store.FindByEmail(ctx, email); err == nil {
		return core.Account{}, core.ErrConflict
	} else if !errors.Is(err, core.ErrNotFound) {
		return core.Account{}, err
	}

	passwordHash, err := HashPassword(input.Password)
	if err != nil {
		return core.Account{}, err
	}
	return deps.Store.CreateAccount(ctx, core.User{
		Email:        email,
		Name:         name,
		PasswordHash: passwordHash,
		Locale:       "en",
		Role:         "owner",
	})
}

// ValidateRegistration checks sign-up input and returns the trimmed name and
// the normalized email, or an InputError.
func ValidateRegistration(input RegistrationInput) (name, email string, err error) {
	name, err = validDisplayName(input.DisplayName)
	if err != nil {
		return "", "", err
	}

	email = core.NormalizeEmail(input.Email)
	if email == "" {
		return "", "", InputError{"email", "Enter your email address."}
	}
	if len(email) > 254 || !utf8.ValidString(email) || strings.ContainsFunc(email, unicode.IsControl) || !validEmail(email) {
		return "", "", InputError{"email", "Enter a valid email address, like name@company.com."}
	}
	if err := validNewPassword(input.Password, input.ConfirmPassword); err != nil {
		return "", "", err
	}
	if !input.AcceptedTerms {
		return "", "", InputError{"terms", "Accept the Terms of Service to continue."}
	}
	return name, email, nil
}

// validDisplayName returns the trimmed name, or an InputError.
func validDisplayName(raw string) (string, error) {
	name := strings.TrimSpace(raw)
	if name == "" {
		return "", InputError{"displayName", "Enter your full name."}
	}
	if !utf8.ValidString(name) || strings.ContainsFunc(name, unicode.IsControl) {
		return "", InputError{"displayName", "Enter a valid name."}
	}
	if utf8.RuneCountInString(name) > 128 {
		return "", InputError{"displayName", "Use a name with 128 characters or fewer."}
	}
	return name, nil
}

// validNewPassword checks a new password and its confirmation, returning an
// InputError or nil.
func validNewPassword(password, confirm string) error {
	if password == "" {
		return InputError{"password", "Enter a password."}
	}
	if !utf8.ValidString(password) {
		return InputError{"password", "Use a valid UTF-8 password."}
	}
	if utf8.RuneCountInString(password) < 12 {
		return InputError{"password", "Use a password with at least 12 characters."}
	}
	if len(password) > 1024 {
		return InputError{"password", "Use a password with 1024 bytes or fewer."}
	}
	if confirm == "" {
		return InputError{"confirmPassword", "Enter your password again."}
	}
	if confirm != password {
		return InputError{"confirmPassword", "Passwords do not match."}
	}
	return nil
}

func validEmail(email string) bool {
	local, domain, ok := strings.Cut(email, "@")
	if !ok || local == "" || domain == "" || !strings.Contains(domain, ".") || strings.HasPrefix(domain, ".") || strings.HasSuffix(domain, ".") || strings.Contains(domain, "..") {
		return false
	}
	address, err := mail.ParseAddress(email)
	return err == nil && address.Address == email
}

// loginAPI signs in with a JSON body and answers with the signed-in user.
func loginAPI(deps core.Deps) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		r.Body = http.MaxBytesReader(w, r.Body, maxLoginBody)
		if mediaType, _, _ := mime.ParseMediaType(r.Header.Get("Content-Type")); mediaType != "application/json" {
			http.Error(w, "unsupported content type", http.StatusUnsupportedMediaType)
			return
		}
		var input struct {
			Email    string `json:"email"`
			Password string `json:"password"`
		}
		if err := json.UnmarshalRead(r.Body, &input); err != nil {
			http.Error(w, "invalid login request", http.StatusBadRequest)
			return
		}
		account, err := startPasswordSession(w, r, deps, input.Email, input.Password)
		if errors.Is(err, errInvalidCredentials) {
			writeJSON(w, http.StatusUnauthorized, apiError{Error: "invalid_credentials"})
			return
		}
		if refused, ok := errors.AsType[tooManyAttempts](err); ok {
			refused.retryAfter(w)
			writeJSON(w, http.StatusTooManyRequests, apiError{Error: "too_many_attempts"})
			return
		}
		if err != nil {
			http.Error(w, "could not create session", http.StatusInternalServerError)
			return
		}
		writeJSON(w, http.StatusOK, loginResponse{User: loginUser{
			ID: account.User.ID, Email: account.User.Email,
			Name: account.User.Name, OnboardingCompleted: account.OnboardingCompleted,
		}})
	}
}

// tooManyAttempts is a sign-in attempt the throttle refused before checking
// the password.
type tooManyAttempts struct{ wait time.Duration }

func (tooManyAttempts) Error() string { return "too many sign-in attempts" }

// retryAfter sets the Retry-After header to the wait in whole seconds and
// returns the message that tells a person how long to wait.
func (e tooManyAttempts) retryAfter(w http.ResponseWriter) string {
	seconds := max(1, int(math.Ceil(e.wait.Seconds())))
	w.Header().Set("Retry-After", strconv.Itoa(seconds))
	if seconds == 1 {
		return "Too many sign-in attempts. Try again in 1 second."
	}
	return fmt.Sprintf("Too many sign-in attempts. Try again in %d seconds.", seconds)
}

// startPasswordSession checks the credentials and starts a session bound to
// the account's current password hash. An unknown email is checked against a
// dummy hash, so the response time does not reveal which accounts exist.
// The throttle counts failed attempts by the normalized email whether or not
// an account uses it, so its refusals do not reveal that either.
func startPasswordSession(w http.ResponseWriter, r *http.Request, deps core.Deps, email, password string) (core.Account, error) {
	if deps.Throttle == nil {
		return core.Account{}, errors.New("sign-in throttling is unavailable")
	}
	email = core.NormalizeEmail(email)
	if wait, allowed := deps.Throttle.Allow(r, email); !allowed {
		return core.Account{}, tooManyAttempts{wait: wait}
	}
	account, err := deps.Store.FindByEmail(r.Context(), email)
	if err != nil && !errors.Is(err, core.ErrNotFound) {
		return core.Account{}, err
	}
	exists := err == nil
	passwordHash := dummyPasswordHash()
	if exists {
		passwordHash = account.User.PasswordHash
	}
	passwordMatches := VerifyPassword(passwordHash, password)
	if !exists || !passwordMatches {
		return core.Account{}, errInvalidCredentials
	}
	deps.Throttle.Forgive(r, email)
	credentialSessions, ok := deps.Sessions.(core.CredentialSessions)
	if !ok {
		return core.Account{}, errors.New("credential-bound sessions are unavailable")
	}
	if err := credentialSessions.StartIfPasswordHash(w, r, account.Principal(), account.User.PasswordHash); err != nil {
		if errors.Is(err, core.ErrConflict) || errors.Is(err, core.ErrUnauthorized) ||
			errors.Is(err, core.ErrForbidden) || errors.Is(err, core.ErrNotFound) {
			return core.Account{}, errInvalidCredentials
		}
		return core.Account{}, err
	}
	return account, nil
}

// logout ends the dashboard session and, with ts-auth, the browser's ts-auth
// session too, so the next sign-in asks for credentials again.
func logout(deps core.Deps, tsAuth *TSAuth) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		if err := deps.Sessions.End(w, r); err != nil {
			http.Error(w, "sign-out could not finish; retry logout", http.StatusServiceUnavailable)
			return
		}
		location := "/login"
		if tsAuth != nil {
			location = tsAuth.endSessionURL(r.Context())
		}
		http.Redirect(w, r, location, http.StatusSeeOther)
	}
}

func currentUser(deps core.Deps) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		principal, ok := currentPrincipal(deps.Sessions, r)
		if !ok {
			writeJSON(w, http.StatusUnauthorized, apiError{Error: "unauthorized"})
			return
		}
		account, err := deps.Store.SnapshotAs(r.Context(), principal)
		if err != nil {
			writeJSON(w, http.StatusUnauthorized, apiError{Error: "unauthorized"})
			return
		}
		writeJSON(w, http.StatusOK, currentUserResponse{User: currentUserData{
			ID: account.User.ID, Email: account.User.Email, Name: account.User.Name,
			Phone: account.User.Phone, Locale: account.User.Locale,
			OnboardingCompleted: account.OnboardingCompleted,
		}})
	}
}

func currentPrincipal(sessions core.Sessions, r *http.Request) (core.Principal, bool) {
	if principal, ok := sessions.Principal(r); ok {
		return principal, true
	}
	return sessions.BearerPrincipal(r)
}

// SafeRedirectTarget is the same-origin path to continue to after signing in
// or signing up, or "/" when raw is empty, longer than 2048 bytes, or leaves
// the dashboard.
func SafeRedirectTarget(raw string) string {
	if raw == "" || len(raw) > 2048 {
		return "/"
	}
	target, err := url.Parse(raw)
	if err != nil || target.IsAbs() || target.Host != "" || target.Opaque != "" || !strings.HasPrefix(target.Path, "/") || strings.HasPrefix(target.Path, "//") || strings.Contains(target.Path, `\`) {
		return "/"
	}
	// Signing in follows this target with a same-origin redirect, which
	// would carry a status message a crafted link put in it.
	if query := target.Query(); core.DropMessages(query) {
		target.RawQuery = query.Encode()
	}
	return target.RequestURI()
}

// signUpURL links to sign-up, passing on the page to return to afterwards.
func signUpURL(returnTo string) string {
	if returnTo == "/" {
		return "/onboarding"
	}
	return "/onboarding?returnTo=" + url.QueryEscape(returnTo)
}

func writeJSON(w http.ResponseWriter, status int, value any) {
	body, err := json.Marshal(value)
	if err != nil {
		http.Error(w, "could not encode response", http.StatusInternalServerError)
		return
	}
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.WriteHeader(status)
	_, _ = w.Write(body)
}
