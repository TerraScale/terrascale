package auth

import (
	"encoding/json/v2"
	"errors"
	"mime"
	"net/http"
	"net/mail"
	"net/url"
	"strings"
	"sync"
	"unicode"
	"unicode/utf8"

	"github.com/a-h/templ"
	"terrascale.tech/dashboard/internal/core"
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

type loginPageData struct {
	Error      string
	RedirectTo string
}

type authStatusPageData struct {
	Title         string
	Description   string
	Message       string
	ShowEmailForm bool
	Flow          bool
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

type InputError string

func (e InputError) Error() string { return string(e) }

var dummyPasswordHash = sync.OnceValue(func() string {
	hash, err := HashPassword("dashboard-invalid-login-dummy-password")
	if err != nil {
		panic(err)
	}
	return hash
})

func Mount(mux *http.ServeMux, deps core.Deps) {
	_ = dummyPasswordHash()

	mux.HandleFunc("GET /login", func(w http.ResponseWriter, r *http.Request) {
		if principal, ok := currentPrincipal(deps.Sessions, r); ok {
			if account, err := deps.Store.SnapshotAs(principal); err == nil {
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
		data := loginPageData{Error: r.URL.Query().Get("error"), RedirectTo: safeRedirectTarget(returnTo)}
		page := core.Page{
			Layout: "auth",
			Title:  "Sign in",
			View: func(p core.Page) templ.Component {
				return loginView(data, p.CSRFToken)
			},
		}
		if err := deps.Render.Page(w, r, page); err != nil {
			http.Error(w, "could not render login page", http.StatusInternalServerError)
		}
	})
	mux.HandleFunc("GET /reset-password", func(w http.ResponseWriter, r *http.Request) {
		renderAuthStatus(w, r, deps, authStatusPageData{
			Title:         "Reset password",
			Description:   "Password recovery is unavailable in the in-memory demo.",
			Message:       "This demo does not send recovery emails or accept password reset tokens. Submitting this form will not send an email.",
			ShowEmailForm: true,
		})
	})
	mux.HandleFunc("POST /reset-password", func(w http.ResponseWriter, r *http.Request) {
		renderAuthStatus(w, r, deps, authStatusPageData{
			Title:         "Reset password",
			Description:   "Password recovery is unavailable in the in-memory demo.",
			Message:       "No recovery request was processed. This demo does not send recovery emails or accept password reset tokens; no password was changed.",
			ShowEmailForm: true,
		})
	})
	mux.HandleFunc("GET /agent/approve", func(w http.ResponseWriter, r *http.Request) {
		renderAuthStatus(w, r, deps, authStatusPageData{
			Title:       "Approve agent access",
			Description: "This in-memory demo cannot verify agent approval requests.",
			Message:     "No verifiable agent request is available, and no access was granted.",
			Flow:        true,
		})
	})
	mux.HandleFunc("POST /agent/approve/submit", func(w http.ResponseWriter, r *http.Request) {
		renderAuthStatus(w, r, deps, authStatusPageData{
			Title:       "Approve agent access",
			Description: "This in-memory demo cannot verify agent approval requests.",
			Message:     "The approval was rejected because no verifiable agent request is available. No access was granted.",
			Flow:        true,
		})
	})
	mux.HandleFunc("GET /cli/approve", func(w http.ResponseWriter, r *http.Request) {
		renderAuthStatus(w, r, deps, authStatusPageData{
			Title:       "Approve CLI access",
			Description: "This in-memory demo cannot verify CLI device requests.",
			Message:     "No verifiable CLI request is available, and no access was granted.",
			Flow:        true,
		})
	})
	mux.HandleFunc("POST /cli/approve/submit", func(w http.ResponseWriter, r *http.Request) {
		renderAuthStatus(w, r, deps, authStatusPageData{
			Title:       "Approve CLI access",
			Description: "This in-memory demo cannot verify CLI device requests.",
			Message:     "The approval was rejected because no verifiable CLI request is available. No access was granted.",
			Flow:        true,
		})
	})
	mux.HandleFunc("GET /oauth/consent", oauthConsent(deps))
	mux.HandleFunc("POST /oauth/consent/submit", func(w http.ResponseWriter, r *http.Request) {
		renderAuthStatus(w, r, deps, authStatusPageData{
			Title:       "Authorize application",
			Description: "This in-memory demo cannot verify OAuth authorization requests.",
			Message:     "The authorization request was rejected. No access was granted.",
			Flow:        true,
		})
	})
	mux.HandleFunc("POST /api/v1/auth/password/login", login(deps))
	mux.HandleFunc("POST /api/v1/auth/logout", logout(deps))
	mux.HandleFunc("GET /api/v1/auth/me", currentUser(deps))
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
		if err := deps.Store.Authorize(principal); err != nil {
			redirectToLogin(w, r)
			return
		}
		renderAuthStatus(w, r, deps, authStatusPageData{
			Title:       "Authorize application",
			Description: "This in-memory demo cannot verify OAuth authorization requests.",
			Message:     "The authorization request is missing or cannot be verified. No access was granted.",
			Flow:        true,
		})
	}
}

func redirectToLogin(w http.ResponseWriter, r *http.Request) {
	returnTo := safeRedirectTarget(r.URL.RequestURI())
	if returnTo == "/" {
		returnTo = "/oauth/consent"
	}
	http.Redirect(w, r, "/login?returnTo="+url.QueryEscape(returnTo), http.StatusSeeOther)
}

func renderAuthStatus(w http.ResponseWriter, r *http.Request, deps core.Deps, data authStatusPageData) {
	page := core.Page{
		Layout: "auth",
		Title:  data.Title,
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
func RegisterAccount(deps core.Deps, input RegistrationInput) (core.Account, error) {
	name := strings.TrimSpace(input.DisplayName)
	if name == "" {
		return core.Account{}, InputError("This field is required.")
	}
	if !utf8.ValidString(name) || strings.ContainsFunc(name, unicode.IsControl) {
		return core.Account{}, InputError("Enter a valid name.")
	}
	if utf8.RuneCountInString(name) > 128 {
		return core.Account{}, InputError("Use a name with 128 characters or fewer.")
	}

	email := core.NormalizeEmail(input.Email)
	if len(email) > 254 || !utf8.ValidString(email) || strings.ContainsFunc(email, unicode.IsControl) || !validEmail(email) {
		return core.Account{}, InputError("Enter a valid email address.")
	}
	if input.Password == "" {
		return core.Account{}, InputError("This field is required.")
	}
	if !utf8.ValidString(input.Password) {
		return core.Account{}, InputError("Use a valid UTF-8 password.")
	}
	if utf8.RuneCountInString(input.Password) < 12 {
		return core.Account{}, InputError("Use at least 12 characters.")
	}
	if len(input.Password) > 1024 {
		return core.Account{}, InputError("Use a password with 1024 bytes or fewer.")
	}
	if input.ConfirmPassword != input.Password {
		return core.Account{}, InputError("Passwords do not match.")
	}
	if !input.AcceptedTerms {
		return core.Account{}, InputError("You must accept the terms to continue.")
	}
	if _, exists := deps.Store.FindByEmail(email); exists {
		return core.Account{}, core.ErrConflict
	}

	passwordHash, err := HashPassword(input.Password)
	if err != nil {
		return core.Account{}, err
	}
	return deps.Store.CreateAccount(core.User{
		Email:        email,
		Name:         name,
		PasswordHash: passwordHash,
		Locale:       "en",
		Role:         "owner",
	})
}

func validEmail(email string) bool {
	local, domain, ok := strings.Cut(email, "@")
	if !ok || local == "" || domain == "" || !strings.Contains(domain, ".") || strings.HasPrefix(domain, ".") || strings.HasSuffix(domain, ".") || strings.Contains(domain, "..") {
		return false
	}
	address, err := mail.ParseAddress(email)
	return err == nil && address.Address == email
}

func login(deps core.Deps) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		r.Body = http.MaxBytesReader(w, r.Body, maxLoginBody)
		var input struct {
			Email      string `json:"email"`
			Password   string `json:"password"`
			RedirectTo string `json:"redirectTo"`
		}
		mediaType, _, _ := mime.ParseMediaType(r.Header.Get("Content-Type"))
		isForm := mediaType == "application/x-www-form-urlencoded" || mediaType == "multipart/form-data"
		if isForm {
			var err error
			if mediaType == "multipart/form-data" {
				err = r.ParseMultipartForm(maxLoginBody)
				if r.MultipartForm != nil {
					defer r.MultipartForm.RemoveAll()
				}
			} else {
				err = r.ParseForm()
			}
			if err != nil {
				http.Error(w, "invalid login form", http.StatusBadRequest)
				return
			}
			input.Email = r.PostForm.Get("email")
			input.Password = r.PostForm.Get("password")
			input.RedirectTo = r.PostForm.Get("redirectTo")
		} else if mediaType == "application/json" {
			if err := json.UnmarshalRead(r.Body, &input); err != nil {
				http.Error(w, "invalid login request", http.StatusBadRequest)
				return
			}
		} else {
			http.Error(w, "unsupported content type", http.StatusUnsupportedMediaType)
			return
		}

		account, exists := deps.Store.FindByEmail(input.Email)
		passwordHash := dummyPasswordHash()
		if exists {
			passwordHash = account.User.PasswordHash
		}
		passwordMatches := VerifyPassword(passwordHash, input.Password)
		if !exists || !passwordMatches {
			if isForm {
				redirectToLoginAfterFormFailure(w, r, input.RedirectTo)
				return
			}
			writeJSON(w, http.StatusUnauthorized, apiError{Error: "invalid_credentials"})
			return
		}

		credentialSessions, ok := deps.Sessions.(core.CredentialSessions)
		if !ok {
			http.Error(w, "credential-bound sessions are unavailable", http.StatusInternalServerError)
			return
		}
		if err := credentialSessions.StartIfPasswordHash(w, r, account.Principal(), account.User.PasswordHash); err != nil {
			if !errors.Is(err, core.ErrConflict) && !errors.Is(err, core.ErrUnauthorized) &&
				!errors.Is(err, core.ErrForbidden) && !errors.Is(err, core.ErrNotFound) {
				http.Error(w, "could not create session", http.StatusInternalServerError)
				return
			}
			if isForm {
				redirectToLoginAfterFormFailure(w, r, input.RedirectTo)
				return
			}
			writeJSON(w, http.StatusUnauthorized, apiError{Error: "invalid_credentials"})
			return
		}
		if isForm {
			location := safeRedirectTarget(input.RedirectTo)
			if location == "/" && !account.OnboardingCompleted {
				location = "/onboarding"
			}
			http.Redirect(w, r, location, http.StatusSeeOther)
			return
		}
		writeJSON(w, http.StatusOK, loginResponse{User: loginUser{
			ID: account.User.ID, Email: account.User.Email,
			Name: account.User.Name, OnboardingCompleted: account.OnboardingCompleted,
		}})
	}
}

func logout(deps core.Deps) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		deps.Sessions.End(w, r)
		http.Redirect(w, r, "/login", http.StatusSeeOther)
	}
}

func currentUser(deps core.Deps) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		principal, ok := currentPrincipal(deps.Sessions, r)
		if !ok {
			writeJSON(w, http.StatusUnauthorized, apiError{Error: "unauthorized"})
			return
		}
		account, err := deps.Store.SnapshotAs(principal)
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

func safeRedirectTarget(raw string) string {
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

func redirectToLoginAfterFormFailure(w http.ResponseWriter, r *http.Request, returnTo string) {
	location := "/login?error=" + url.QueryEscape("Incorrect email or password.") +
		"&returnTo=" + url.QueryEscape(safeRedirectTarget(returnTo))
	http.Redirect(w, r, location, http.StatusSeeOther)
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
