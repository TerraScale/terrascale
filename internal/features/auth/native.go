package auth

import (
	"context"
	"encoding/json/v2"
	"errors"
	"fmt"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"time"
	"unicode/utf8"

	"github.com/a-h/templ"
	"golang.org/x/oauth2"
	"terrascale.tech/dashboard/internal/core"
)

// codePageData is sign-in with an emailed code: the email step asks where to
// send one, the code step takes it. SignUp words the code step for verifying
// a new account's address, and Name is the one given at sign-up, which ts-auth
// does not keep.
type codePageData struct {
	CodeStep          bool
	SignUp            bool
	Name              string
	Email             string
	RedirectTo        string
	Error             string
	CodeError         string
	ResendSeconds     int
	ResendUnavailable bool
	Notice            string
}

// resetPageData is password recovery: the email step asks where to send a
// code, the code step takes it with the new password.
type resetPageData struct {
	CodeStep   bool
	Email      string
	Error      string
	ErrorField string
}

// passwordResetNotice is the login page's notice after a password reset,
// named rather than carried as text so no link can put words on the page.
const passwordResetNotice = "password-reset"

// unavailable is the message for a ts-auth outage or refusal the visitor
// cannot fix.
const unavailable = "Sign-in is unavailable right now. Try again in a moment."

// mountNative registers the dashboard's own sign-in, code and recovery pages,
// which check credentials through ts-auth's headless API. Only external
// identity providers leave the dashboard, through explicit single sign-on.
func (t *TSAuth) mountNative(mux *http.ServeMux, deps core.Deps) {
	mux.HandleFunc("GET /login/sso", func(w http.ResponseWriter, r *http.Request) {
		returnTo := SafeRedirectTarget(r.URL.Query().Get("returnTo"))
		if err := t.signIn(w, r, returnTo); err != nil {
			signInFailed(w, r, deps, http.StatusServiceUnavailable, "Single sign-on is unavailable right now. Try again in a moment.", returnTo, err)
		}
	})
	mux.HandleFunc("GET /login/callback", t.callback(deps))
	mux.HandleFunc("POST /login", t.passwordSignIn(deps))
	mux.HandleFunc("GET /login/code", func(w http.ResponseWriter, r *http.Request) {
		renderCode(w, r, deps, http.StatusOK, codePageData{
			Email: r.URL.Query().Get("email"), RedirectTo: SafeRedirectTarget(r.URL.Query().Get("returnTo")),
		})
	})
	mux.HandleFunc("POST /login/code", t.sendCode(deps))
	mux.HandleFunc("POST /login/code/verify", t.verifyCode(deps))
	mux.HandleFunc("POST /login/code/resend", t.resendSignUpCode(deps))
	mux.HandleFunc("GET /reset-password", func(w http.ResponseWriter, r *http.Request) {
		renderReset(w, r, deps, http.StatusOK, resetPageData{})
	})
	mux.HandleFunc("POST /reset-password", t.startReset(deps))
	mux.HandleFunc("POST /reset-password/complete", t.completeReset(deps))
}

// passwordSignIn checks an email and password with ts-auth. Every refusal
// reads the same whether or not the account exists.
func (t *TSAuth) passwordSignIn(deps core.Deps) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		r.Body = http.MaxBytesReader(w, r.Body, maxLoginBody)
		if err := r.ParseForm(); err != nil {
			http.Error(w, "invalid login form", http.StatusBadRequest)
			return
		}
		data := loginPageData{
			Email: strings.TrimSpace(r.PostForm.Get("email")), RedirectTo: SafeRedirectTarget(r.PostForm.Get("redirectTo")), TSAuth: true,
			PromptLogin: r.PostForm.Get("prompt") == "login",
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
		email := core.NormalizeEmail(data.Email)
		token, err := t.attempt(r, deps, email, func(client string) (*oauth2.Token, error) {
			token, err := t.grant(r, client, passwordGrant, url.Values{"email": {email}, "password": {password}})
			return token, refusedAs(err, "invalid_grant", errInvalidCredentials)
		})
		switch refused, throttled := errors.AsType[tooManyAttempts](err); {
		case throttled:
			data.Error = refused.retryAfter(w)
			renderLogin(w, r, deps, http.StatusTooManyRequests, data)
		case errors.Is(err, errInvalidCredentials):
			data.Error = "Incorrect email or password."
			renderLogin(w, r, deps, http.StatusUnauthorized, data)
		case err != nil:
			data.Error = withReference(unavailable, logFailure("ts-auth password sign-in", err))
			renderLogin(w, r, deps, http.StatusServiceUnavailable, data)
		default:
			t.finishSignIn(w, r, deps, token, "", data.RedirectTo, func(status int, message string) {
				data.Error = message
				renderLogin(w, r, deps, status, data)
			})
		}
	}
}

// attempt tries a secret that belongs to email with ts-auth, after the
// dashboard's throttle allows one more attempt for the client and the email,
// and forgives the attempt when ts-auth accepts it. ts-auth limits attempts
// too; this keeps a flood from reaching it.
func (t *TSAuth) attempt(r *http.Request, deps core.Deps, email string, try func(client string) (*oauth2.Token, error)) (*oauth2.Token, error) {
	if wait, allowed := deps.Throttle.Allow(r, email); !allowed {
		return nil, tooManyAttempts{wait: wait}
	}
	token, err := try(deps.Throttle.ClientAddr(r))
	if err == nil {
		deps.Throttle.Forgive(r, email)
	}
	return token, err
}

// mailed asks ts-auth to email the address, after the dashboard's throttle
// allows one more message for the client and the address.
func (t *TSAuth) mailed(r *http.Request, deps core.Deps, email, path string, fields map[string]string) ([]byte, error) {
	if wait, allowed := deps.Throttle.Allow(r, "mail "+email); !allowed {
		return nil, tooManyAttempts{wait: wait}
	}
	fields["email"] = email
	return t.headless(r, deps.Throttle.ClientAddr(r), path, fields)
}

// signUpCookie keeps a sign-up's pending token from the request to the code
// step: ts-auth opens the account only for the requester who holds it.
// HttpOnly keeps the token out of the page, and the __Host- prefix keeps a
// sibling subdomain from planting one.
func (t *TSAuth) signUpCookie() string {
	if t.cfg.Secure {
		return "__Host-ts_dashboard_signup"
	}
	return "ts_dashboard_signup"
}

func (t *TSAuth) setSignUpCookie(w http.ResponseWriter, value string, maxAge int) {
	http.SetCookie(w, &http.Cookie{
		Name: t.signUpCookie(), Value: value, Path: "/", MaxAge: maxAge, HttpOnly: true, Secure: t.cfg.Secure, SameSite: http.SameSiteLaxMode,
	})
}

// finishSignIn starts a session for ts-auth's token answer and continues to
// returnTo, or reports why it could not. name names an account this sign-in
// opens.
func (t *TSAuth) finishSignIn(w http.ResponseWriter, r *http.Request, deps core.Deps, token *oauth2.Token, name, returnTo string, failed func(int, string)) {
	location, err := t.startSession(w, r, deps, token, "", name, returnTo)
	if err != nil {
		status, message := sessionFailure(err)
		failed(status, withReference(message, logFailure("ts-auth sign-in", err)))
		return
	}
	http.Redirect(w, r, location, http.StatusSeeOther)
}

// sendCode emails a sign-in code. The code step follows whether or not the
// address has an account, so the page never tells.
func (t *TSAuth) sendCode(deps core.Deps) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		r.Body = http.MaxBytesReader(w, r.Body, maxLoginBody)
		if err := r.ParseForm(); err != nil {
			http.Error(w, "invalid code request", http.StatusBadRequest)
			return
		}
		data := codePageData{
			Email: core.NormalizeEmail(r.PostForm.Get("email")), RedirectTo: SafeRedirectTarget(r.PostForm.Get("redirectTo")),
			SignUp: r.PostForm.Get("signUp") == "1", Name: signUpName(r.PostForm.Get("name")),
		}
		if !usableEmail(data.Email) {
			data.Error = "Enter a valid email address, like name@company.com."
			renderCode(w, r, deps, http.StatusUnprocessableEntity, data)
			return
		}
		_, err := t.mailed(r, deps, data.Email, headlessCodeStart, map[string]string{})
		if refused, ok := errors.AsType[tooManyAttempts](err); ok {
			data.Error = refused.retryAfter(w)
			renderCode(w, r, deps, http.StatusTooManyRequests, data)
			return
		}
		if err != nil {
			data.Error = withReference(unavailable, logFailure("ts-auth email code", err))
			renderCode(w, r, deps, http.StatusServiceUnavailable, data)
			return
		}
		data.CodeStep = true
		renderCode(w, r, deps, http.StatusOK, data)
	}
}

// verifyCode signs in with an emailed code. A sign-up's code opens the
// account at ts-auth as it signs in.
func (t *TSAuth) verifyCode(deps core.Deps) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		r.Body = http.MaxBytesReader(w, r.Body, maxLoginBody)
		if err := r.ParseForm(); err != nil {
			http.Error(w, "invalid code", http.StatusBadRequest)
			return
		}
		data := codePageData{
			CodeStep: true, Email: core.NormalizeEmail(r.PostForm.Get("email")),
			RedirectTo: SafeRedirectTarget(r.PostForm.Get("redirectTo")),
			SignUp:     r.PostForm.Get("signUp") == "1", Name: signUpName(r.PostForm.Get("name")),
		}
		if data.SignUp {
			if retry, err := t.readSignUpRetry(r); err == nil && retry.Email == data.Email {
				data.ResendSeconds = retry.remaining()
			} else {
				data.ResendUnavailable = true
			}
		}
		code := emailedCode(r.PostForm.Get("code"))
		if !usableEmail(data.Email) {
			data.CodeStep, data.Error = false, "Enter a valid email address, like name@company.com."
			renderCode(w, r, deps, http.StatusUnprocessableEntity, data)
			return
		}
		if code == "" {
			data.CodeError = "Enter the code from the email."
			renderCode(w, r, deps, http.StatusUnprocessableEntity, data)
			return
		}
		fields := url.Values{"email": {data.Email}, "code": {code}}
		// A sign-up's code opens the account only with its pending token;
		// ts-auth ignores the token for a sign-in code.
		if pending, err := r.Cookie(t.signUpCookie()); err == nil && pending.Value != "" {
			fields.Set("pending_token", pending.Value)
		}
		token, err := t.attempt(r, deps, data.Email, func(client string) (*oauth2.Token, error) {
			token, err := t.grant(r, client, emailCodeGrant, fields)
			return token, refusedAs(err, "invalid_grant", errInvalidCode)
		})
		if err == nil {
			t.setSignUpCookie(w, "", -1)
			t.setSignUpRetryCookie(w, "", -1)
		}
		switch refused, throttled := errors.AsType[tooManyAttempts](err); {
		case throttled:
			data.Error = refused.retryAfter(w)
			renderCode(w, r, deps, http.StatusTooManyRequests, data)
		case errors.Is(err, errInvalidCode):
			data.CodeError = "That code is wrong or has expired. Check the latest email, or send a new code."
			renderCode(w, r, deps, http.StatusUnauthorized, data)
		case err != nil:
			data.Error = withReference(unavailable, logFailure("ts-auth email code sign-in", err))
			renderCode(w, r, deps, http.StatusServiceUnavailable, data)
		default:
			t.finishSignIn(w, r, deps, token, data.Name, data.RedirectTo, func(status int, message string) {
				data.Error = message
				renderCode(w, r, deps, status, data)
			})
		}
	}
}

// SignUp asks ts-auth to email a code that verifies the address, and shows
// the step that takes it; redeeming the code opens the account with the
// password given here. The step follows whether or not the address is taken:
// ts-auth emails its owner a sign-in code instead, so the page never tells.
// It returns an InputError the form shows, or an error wrapping
// core.ErrUnavailable.
func (t *TSAuth) SignUp(w http.ResponseWriter, r *http.Request, deps core.Deps, input RegistrationInput, returnTo string) error {
	name, email, err := ValidateRegistration(input)
	if err != nil {
		return err
	}
	if deps.SignUpMail == nil {
		return core.ErrUnavailable
	}
	if wait, allowed := deps.SignUpMail.Allow(r, email); !allowed {
		seconds := resendSeconds(wait)
		w.Header().Set("Retry-After", strconv.Itoa(seconds))
		return InputError{Message: fmt.Sprintf("Please wait %d seconds before resending the code.", seconds)}
	}
	answer, err := t.mailed(r, deps, email, headlessSignUp, map[string]string{"password": input.Password})
	if refused, ok := errors.AsType[tooManyAttempts](err); ok {
		// The sign-up budget has already reserved its minute, even when
		// another mail limit asks for a shorter wait.
		refused.wait = max(refused.wait, time.Minute)
		return InputError{Message: refused.retryAfter(w)}
	}
	if err = passwordRefused(err, "password"); err != nil {
		if _, ok := errors.AsType[InputError](err); !ok {
			logFailure("ts-auth sign-up", err)
			err = core.ErrUnavailable
		}
		return err
	}
	var pending struct {
		Token string `json:"pending_token"`
	}
	if json.Unmarshal(answer, &pending) != nil || pending.Token == "" {
		logFailure("ts-auth sign-up", errors.New("ts-auth answered without a pending token"))
		return core.ErrUnavailable
	}
	now := time.Now()
	retry := signUpRetry{
		Name: name, Email: email, Password: input.Password, Token: pending.Token,
		ReturnTo: SafeRedirectTarget(returnTo), Expires: now.Add(time.Hour), ResendAt: now.Add(time.Minute),
	}
	sealed, err := t.sealSignUpRetry(retry)
	t.setSignUpCookie(w, pending.Token, int(time.Hour.Seconds()))
	data := retry.pageData()
	if r.URL.Path == "/login/code/resend" {
		data.Notice = "Check your email for a new code."
	}
	if err != nil {
		// The email has already been sent. Preserve verification even if
		// the request is too large for the optional resend cookie.
		t.setSignUpRetryCookie(w, "", -1)
		data.ResendUnavailable = true
		data.Notice = "Check your email for the verification code. To request another code, create your account again."
	} else {
		t.setSignUpRetryCookie(w, sealed, int(time.Hour.Seconds()))
	}
	renderCode(w, r, deps, http.StatusOK, data)
	return nil
}

// startReset emails a password reset code. The code step follows whether or
// not the address has an account.
func (t *TSAuth) startReset(deps core.Deps) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		r.Body = http.MaxBytesReader(w, r.Body, maxLoginBody)
		if err := r.ParseForm(); err != nil {
			http.Error(w, "invalid reset request", http.StatusBadRequest)
			return
		}
		data := resetPageData{Email: core.NormalizeEmail(r.PostForm.Get("email"))}
		if !usableEmail(data.Email) {
			data.ErrorField, data.Error = "email", "Enter a valid email address, like name@company.com."
			renderReset(w, r, deps, http.StatusUnprocessableEntity, data)
			return
		}
		_, err := t.mailed(r, deps, data.Email, headlessResetStart, map[string]string{})
		if refused, ok := errors.AsType[tooManyAttempts](err); ok {
			data.Error = refused.retryAfter(w)
			renderReset(w, r, deps, http.StatusTooManyRequests, data)
			return
		}
		if err != nil {
			data.Error = withReference("Password recovery is unavailable right now. Try again in a moment.", logFailure("ts-auth password reset", err))
			renderReset(w, r, deps, http.StatusServiceUnavailable, data)
			return
		}
		data.CodeStep = true
		renderReset(w, r, deps, http.StatusOK, data)
	}
}

// completeReset sets a new password with the emailed code, which signs the
// user out everywhere, and sends the visitor to sign in with it.
func (t *TSAuth) completeReset(deps core.Deps) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		r.Body = http.MaxBytesReader(w, r.Body, maxLoginBody)
		if err := r.ParseForm(); err != nil {
			http.Error(w, "invalid password reset", http.StatusBadRequest)
			return
		}
		data := resetPageData{CodeStep: true, Email: core.NormalizeEmail(r.PostForm.Get("email"))}
		code, password := emailedCode(r.PostForm.Get("code")), r.PostForm.Get("password")
		passwordErr, badPassword := errors.AsType[InputError](validNewPassword(password, r.PostForm.Get("confirmPassword")))
		switch {
		case !usableEmail(data.Email):
			data.CodeStep, data.ErrorField, data.Error = false, "email", "Enter a valid email address, like name@company.com."
		case code == "":
			data.ErrorField, data.Error = "code", "Enter the code from the email."
		case badPassword:
			data.ErrorField, data.Error = passwordErr.Field, passwordErr.Message
		}
		if data.Error != "" {
			renderReset(w, r, deps, http.StatusUnprocessableEntity, data)
			return
		}
		_, err := t.attempt(r, deps, data.Email, func(client string) (*oauth2.Token, error) {
			_, err := t.headless(r, client, headlessResetComplete, map[string]string{"email": data.Email, "code": code, "new_password": password})
			return nil, passwordRefused(refusedAs(err, "invalid_code", errInvalidCode), "password")
		})
		passwordErr, badPassword = errors.AsType[InputError](err)
		switch refused, throttled := errors.AsType[tooManyAttempts](err); {
		case throttled:
			data.Error = refused.retryAfter(w)
			renderReset(w, r, deps, http.StatusTooManyRequests, data)
		case errors.Is(err, errInvalidCode):
			data.ErrorField, data.Error = "code", "That code is wrong or has expired. Check the latest email, or request a new code."
			renderReset(w, r, deps, http.StatusUnauthorized, data)
		case badPassword:
			data.ErrorField, data.Error = passwordErr.Field, passwordErr.Message
			renderReset(w, r, deps, http.StatusUnprocessableEntity, data)
		case err != nil:
			data.Error = withReference("Password recovery is unavailable right now. Try again in a moment.", logFailure("ts-auth password reset", err))
			renderReset(w, r, deps, http.StatusServiceUnavailable, data)
		default:
			if err := endDashboardSessions(r.Context(), deps.Store, data.Email); err != nil {
				// The new password is set; only the sign-out failed, and
				// another reset retries it.
				data.CodeStep, data.ErrorField = false, ""
				data.Error = withReference("Your password was changed, but your other dashboard sessions could not be signed out. Request another reset code to try again.",
					logFailure("end sessions after password reset", err))
				renderReset(w, r, deps, http.StatusServiceUnavailable, data)
				return
			}
			http.Redirect(w, r, "/login?"+url.Values{"notice": {passwordResetNotice}, "email": {data.Email}}.Encode(), http.StatusSeeOther)
		}
	}
}

// endDashboardSessions ends every dashboard session and personal access
// token of the address's account, as ts-auth does after a reset, rather than
// at each session's next recheck, which a session without a refresh token
// never reaches. Rotating the account's unused local password hash bumps its
// credential version in one write, and every session and token is checked
// against it. An address without a dashboard account has nothing to end.
func endDashboardSessions(ctx context.Context, store core.Store, email string) error {
	account, err := store.FindByEmail(ctx, email)
	if errors.Is(err, core.ErrNotFound) {
		return nil
	}
	if err != nil {
		return err
	}
	hash, err := HashPassword(oauth2.GenerateVerifier())
	if err != nil {
		return err
	}
	return store.Update(ctx, account.ID, func(a *core.Account) error {
		a.User.PasswordHash = hash
		return nil
	})
}

// usableEmail reports whether a normalized email can be sent to.
func usableEmail(email string) bool {
	return email != "" && len(email) <= 254 && validEmail(email)
}

// emailedCode is a typed code without the spaces a person may add, or empty
// when it cannot be a code.
func emailedCode(raw string) string {
	code := strings.Join(strings.Fields(raw), "")
	if len(code) > 64 || !utf8.ValidString(code) {
		return ""
	}
	return code
}

// signUpName is a name given at sign-up and carried through the code step,
// or empty when it is not one sign-up accepts.
func signUpName(raw string) string {
	name, err := validDisplayName(raw)
	if err != nil {
		return ""
	}
	return name
}

func renderCode(w http.ResponseWriter, r *http.Request, deps core.Deps, status int, data codePageData) {
	title := "Sign in with a code"
	if data.SignUp {
		title = "Verify your email"
	}
	renderNative(w, r, deps, status, title, func(csrf string) templ.Component { return codeView(data, csrf) })
}

func renderReset(w http.ResponseWriter, r *http.Request, deps core.Deps, status int, data resetPageData) {
	renderNative(w, r, deps, status, "Reset password", func(csrf string) templ.Component { return resetView(data, csrf) })
}

func renderNative(w http.ResponseWriter, r *http.Request, deps core.Deps, status int, title string, view func(string) templ.Component) {
	page := core.Page{
		Layout: "auth", Title: title, Status: status,
		View: func(p core.Page) templ.Component { return view(p.CSRFToken) },
	}
	if err := deps.Render.Page(w, r, page); err != nil {
		http.Error(w, "could not render "+strings.ToLower(title)+" page", http.StatusInternalServerError)
	}
}
