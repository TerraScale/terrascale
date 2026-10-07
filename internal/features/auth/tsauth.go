package auth

import (
	"context"
	"crypto/rand"
	"crypto/subtle"
	"encoding/base64"
	"encoding/json/v2"
	"errors"
	"fmt"
	"log/slog"
	"net/http"
	"net/url"
	"slices"
	"strings"
	"sync"
	"time"
	"unicode"
	"unicode/utf8"

	"github.com/coreos/go-oidc/v3/oidc"
	"golang.org/x/oauth2"
	"golang.org/x/sync/singleflight"
	"terrascale.tech/dashboard/internal/core"
)

// TSAuthConfig registers the dashboard as a confidential OpenID Connect
// client of ts-auth.
type TSAuthConfig struct {
	Issuer       string
	ClientID     string
	ClientSecret string
	// RedirectURL is the dashboard's absolute /login/callback URL.
	RedirectURL string
	// Secure sends the sign-in cookie over HTTPS only.
	Secure bool
}

// TSAuth signs users in through ts-auth with the authorization code flow,
// PKCE (S256), state and nonce, rechecks their sessions with refresh tokens,
// and signs them out of ts-auth with RP-initiated logout. It discovers
// ts-auth on first use, so the dashboard starts while ts-auth is down.
type TSAuth struct {
	cfg    TSAuthConfig
	client *http.Client
	// recheck is how long discovery without the headless grants is trusted
	// before it is fetched again.
	recheck time.Duration

	mu         sync.Mutex
	provider   *tsAuthProvider
	discovered time.Time
	// fetches shares one discovery fetch among the requests waiting for it.
	fetches singleflight.Group
}

type tsAuthProvider struct {
	oauth    oauth2.Config
	verifier *oidc.IDTokenVerifier
	// keys are the issuer's signing keys, for resource tokens
	// (resource_token.go).
	keys       oidc.KeySet
	endSession string
	revocation string
	// headless is whether discovery lists the headless grants, which the
	// dashboard's own sign-in pages need.
	headless bool
}

// signInFlow is one sign-in in progress, kept in a short-lived cookie from
// the redirect to ts-auth until the callback.
type signInFlow struct {
	State    string `json:"state"`
	Nonce    string `json:"nonce"`
	Verifier string `json:"verifier"`
	ReturnTo string `json:"returnTo"`
}

// refreshState is a session's Refresh: the ts-auth refresh token and the
// subject every ID token it yields must name.
type refreshState struct {
	Subject string `json:"sub"`
	Token   string `json:"token"`
}

// refreshableSessions starts sessions that ts-auth rechecks.
type refreshableSessions interface {
	StartRefreshable(w http.ResponseWriter, r *http.Request, p core.Principal, refresh string, refreshAt time.Time) error
}

const (
	signInFlowLifetime = 10 * time.Minute
	// minRecheck is the shortest wait before a session is rechecked with
	// ts-auth, used too when ts-auth does not say when its token expires.
	minRecheck = 5 * time.Minute
)

var (
	// errSignInRejected is a callback that did not prove a ts-auth sign-in.
	errSignInRejected = errors.New("ts-auth sign-in rejected")
	// errEmailUnusable is a ts-auth user whose email cannot open an account:
	// it is unverified, or its account is linked to another ts-auth user.
	errEmailUnusable = errors.New("ts-auth email is unverified or linked to another ts-auth user")
)

// NewTSAuth checks that the registration is complete, so a misconfigured
// dashboard fails at startup rather than at the first sign-in.
func NewTSAuth(cfg TSAuthConfig) (*TSAuth, error) {
	redirect, err := url.Parse(cfg.RedirectURL)
	if cfg.Issuer == "" || cfg.ClientID == "" || cfg.ClientSecret == "" || err != nil || !redirect.IsAbs() || redirect.Host == "" {
		return nil, errors.New("ts-auth needs an issuer, a client ID and secret, and an absolute redirect URL")
	}
	// Redirects are not followed: a redirected token request would resend
	// a password or code, and the client's credentials, elsewhere.
	client := &http.Client{Timeout: 10 * time.Second, CheckRedirect: func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse }}
	return &TSAuth{cfg: cfg, client: client, recheck: time.Minute}, nil
}

// discover returns ts-auth's endpoints and keys, fetching them on the first
// call and again after a failed one. Until discovery lists the headless
// grants it is fetched again every recheck, so ts-auth gaining them takes
// effect without a restart; a failed refetch keeps what worked.
//
// No lock is held while ts-auth answers: a recheck is claimed by the request
// that finds it due, and every other request keeps the cached provider
// meanwhile.
func (t *TSAuth) discover(ctx context.Context) (*tsAuthProvider, error) {
	t.mu.Lock()
	cached := t.provider
	if cached != nil && (cached.headless || time.Since(t.discovered) < t.recheck) {
		t.mu.Unlock()
		return cached, nil
	}
	t.discovered = time.Now()
	t.mu.Unlock()
	fetched, err, _ := t.fetches.Do("discovery", func() (any, error) {
		return t.fetchDiscovery(context.WithoutCancel(ctx))
	})
	if err != nil {
		if cached != nil {
			slog.Warn("recheck ts-auth discovery", "error", err)
			return cached, nil
		}
		return nil, err
	}
	provider := fetched.(*tsAuthProvider)
	t.mu.Lock()
	t.provider, t.discovered = provider, time.Now()
	t.mu.Unlock()
	return provider, nil
}

// Headless reports whether ts-auth offers the headless API the dashboard's
// own sign-in pages use. Without it, signing in and up keeps redirecting to
// ts-auth, so the dashboard works before and after ts-auth ships it.
func (t *TSAuth) Headless(ctx context.Context) bool {
	provider, err := t.discover(ctx)
	return err == nil && provider.headless
}

func (t *TSAuth) fetchDiscovery(ctx context.Context) (*tsAuthProvider, error) {
	provider, err := oidc.NewProvider(oidc.ClientContext(ctx, t.client), t.cfg.Issuer)
	if err != nil {
		return nil, fmt.Errorf("discover ts-auth: %w", err)
	}
	var metadata struct {
		EndSession string   `json:"end_session_endpoint"`
		Revocation string   `json:"revocation_endpoint"`
		PKCE       []string `json:"code_challenge_methods_supported"`
		Grants     []string `json:"grant_types_supported"`
		JWKS       string   `json:"jwks_uri"`
	}
	if err := provider.Claims(&metadata); err != nil {
		return nil, fmt.Errorf("discover ts-auth: %w", err)
	}
	if !slices.Contains(metadata.PKCE, "S256") {
		return nil, errors.New("discover ts-auth: PKCE with S256 is not supported")
	}
	return &tsAuthProvider{
		oauth: oauth2.Config{
			ClientID: t.cfg.ClientID, ClientSecret: t.cfg.ClientSecret, RedirectURL: t.cfg.RedirectURL,
			Endpoint: provider.Endpoint(),
			Scopes:   []string{oidc.ScopeOpenID, "email", "profile", oidc.ScopeOfflineAccess},
		},
		verifier:   provider.Verifier(&oidc.Config{ClientID: t.cfg.ClientID}),
		keys:       oidc.NewRemoteKeySet(oidc.ClientContext(context.Background(), t.client), metadata.JWKS),
		endSession: metadata.EndSession,
		revocation: metadata.Revocation,
		headless:   slices.Contains(metadata.Grants, passwordGrant) && slices.Contains(metadata.Grants, emailCodeGrant),
	}, nil
}

// flowCookie is the sign-in flow cookie's name. The __Host- prefix keeps a
// sibling subdomain from planting its own flow to sign a visitor in to
// another account.
func (t *TSAuth) flowCookie() string {
	if t.cfg.Secure {
		return "__Host-ts_dashboard_signin"
	}
	return "ts_dashboard_signin"
}

// signIn sends the browser to ts-auth, which returns it to the callback.
// prompt=login, which switching accounts asks for, makes ts-auth ask for
// credentials even when the browser is already signed in to it.
func (t *TSAuth) signIn(w http.ResponseWriter, r *http.Request, returnTo string) error {
	provider, err := t.discover(r.Context())
	if err != nil {
		return err
	}
	flow := signInFlow{
		State: oauth2.GenerateVerifier(), Nonce: oauth2.GenerateVerifier(),
		Verifier: oauth2.GenerateVerifier(), ReturnTo: returnTo,
	}
	value, err := json.Marshal(flow)
	if err != nil {
		return err
	}
	http.SetCookie(w, &http.Cookie{
		Name: t.flowCookie(), Value: base64.RawURLEncoding.EncodeToString(value), Path: "/",
		MaxAge: int(signInFlowLifetime.Seconds()), HttpOnly: true, Secure: t.cfg.Secure, SameSite: http.SameSiteLaxMode,
	})
	options := []oauth2.AuthCodeOption{oauth2.S256ChallengeOption(flow.Verifier), oidc.Nonce(flow.Nonce)}
	if r.URL.Query().Get("prompt") == "login" {
		options = append(options, oauth2.SetAuthURLParam("prompt", "login"))
	}
	http.Redirect(w, r, provider.oauth.AuthCodeURL(flow.State, options...), http.StatusSeeOther)
	return nil
}

// takeFlow returns the sign-in flow the browser started and clears it, so a
// callback can be answered once.
func (t *TSAuth) takeFlow(w http.ResponseWriter, r *http.Request) (signInFlow, bool) {
	cookie, err := r.Cookie(t.flowCookie())
	if err != nil {
		return signInFlow{}, false
	}
	http.SetCookie(w, &http.Cookie{
		Name: t.flowCookie(), Path: "/", MaxAge: -1, HttpOnly: true, Secure: t.cfg.Secure, SameSite: http.SameSiteLaxMode,
	})
	value, err := base64.RawURLEncoding.DecodeString(cookie.Value)
	var flow signInFlow
	if err != nil || json.Unmarshal(value, &flow) != nil || flow.State == "" || flow.Nonce == "" || flow.Verifier == "" {
		return signInFlow{}, false
	}
	return flow, true
}

// callback finishes a sign-in: it checks that ts-auth answered the flow this
// browser started, redeems the code with the PKCE verifier, validates the ID
// token and its nonce, and starts a session for the user's account.
func (t *TSAuth) callback(deps core.Deps) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		query := r.URL.Query()
		flow, ok := t.takeFlow(w, r)
		if !ok || subtle.ConstantTimeCompare([]byte(query.Get("state")), []byte(flow.State)) != 1 {
			signInFailed(w, r, deps, http.StatusBadRequest, "This sign-in link expired or was started in another browser. Sign in again.", "/",
				errors.New("no sign-in flow matches the callback's state"))
			return
		}
		if query.Get("error") != "" {
			signInFailed(w, r, deps, http.StatusUnauthorized, "ts-auth did not sign you in. Sign in again.", flow.ReturnTo,
				fmt.Errorf("ts-auth refused the sign-in: %q", query.Get("error")))
			return
		}
		token, err := t.exchange(r.Context(), query.Get("code"), flow)
		var location string
		if err == nil {
			location, err = t.startSession(w, r, deps, token, flow.Nonce, "", flow.ReturnTo)
		}
		if err != nil {
			status, message := sessionFailure(err)
			signInFailed(w, r, deps, status, message, flow.ReturnTo, err)
			return
		}
		http.Redirect(w, r, location, http.StatusSeeOther)
	}
}

// startSession signs the user ts-auth's token answer names in to their
// account and returns where to continue. nonce is the one the redirect flow
// sent, empty for the headless API's answers; name, when set, names an
// account this sign-in opens if ts-auth has no name for the user.
func (t *TSAuth) startSession(w http.ResponseWriter, r *http.Request, deps core.Deps, token *oauth2.Token, nonce, name, returnTo string) (string, error) {
	identity, refresh, refreshAt, err := t.identify(r.Context(), token, nonce)
	if err != nil {
		return "", err
	}
	if name != "" && identity.Name == identity.Email {
		identity.Name = name
	}
	account, err := tsAuthAccount(r.Context(), deps.Store, identity)
	if err != nil {
		return "", err
	}
	sessions, ok := deps.Sessions.(refreshableSessions)
	if !ok {
		return "", errors.New("refreshable sessions are unavailable")
	}
	if err := sessions.StartRefreshable(w, r, account.Principal(), refresh, refreshAt); err != nil {
		return "", fmt.Errorf("start session: %w", err)
	}
	location := SafeRedirectTarget(returnTo)
	if location == "/" && !account.OnboardingCompleted {
		location = "/onboarding"
	}
	return location, nil
}

// sessionFailure is the status and message for a sign-in startSession, or
// the code exchange before it, could not finish.
func sessionFailure(err error) (int, string) {
	switch {
	case errors.Is(err, errEmailUnusable):
		return http.StatusForbidden, "Your ts-auth email address must be verified, and not linked to another ts-auth user, to sign in."
	case errors.Is(err, errSignInRejected):
		return http.StatusUnauthorized, "ts-auth could not confirm this sign-in. Sign in again."
	default:
		return http.StatusServiceUnavailable, "Sign-in could not finish right now. Try again in a moment."
	}
}

// tsAuthIdentity is who an ID token says signed in.
type tsAuthIdentity struct {
	Subject string
	Email   string
	Name    string
}

// exchange redeems the authorization code with the flow's PKCE verifier.
// A refusal wraps errSignInRejected.
func (t *TSAuth) exchange(ctx context.Context, code string, flow signInFlow) (*oauth2.Token, error) {
	provider, err := t.discover(ctx)
	if err != nil {
		return nil, err
	}
	token, err := provider.oauth.Exchange(oidc.ClientContext(ctx, t.client), code, oauth2.VerifierOption(flow.Verifier))
	if _, refused := errors.AsType[*oauth2.RetrieveError](err); refused {
		return nil, fmt.Errorf("%w: %w", errSignInRejected, err)
	}
	return token, err
}

// identify returns the identity the token's ID token proves, with the session's
// refresh state and when to recheck it. A nonce, when not empty, must be the
// ID token's. Failures ts-auth's answer caused wrap errSignInRejected or are
// errEmailUnusable.
func (t *TSAuth) identify(ctx context.Context, token *oauth2.Token, nonce string) (tsAuthIdentity, string, time.Time, error) {
	provider, err := t.discover(ctx)
	if err != nil {
		return tsAuthIdentity{}, "", time.Time{}, err
	}
	if token == nil {
		return tsAuthIdentity{}, "", time.Time{}, fmt.Errorf("%w: ts-auth answered without tokens", errSignInRejected)
	}
	raw, _ := token.Extra("id_token").(string)
	// Verify checks the signature against ts-auth's published keys with an
	// algorithm it advertises, the issuer, the audience and the expiry.
	idToken, err := provider.verifier.Verify(oidc.ClientContext(ctx, t.client), raw)
	if err != nil {
		return tsAuthIdentity{}, "", time.Time{}, fmt.Errorf("%w: %w", errSignInRejected, err)
	}
	if nonce != "" && subtle.ConstantTimeCompare([]byte(idToken.Nonce), []byte(nonce)) != 1 {
		return tsAuthIdentity{}, "", time.Time{}, fmt.Errorf("%w: ID token nonce does not match", errSignInRejected)
	}
	var claims struct {
		Email         string `json:"email"`
		EmailVerified bool   `json:"email_verified"`
		Name          string `json:"name"`
	}
	if err := idToken.Claims(&claims); err != nil {
		return tsAuthIdentity{}, "", time.Time{}, fmt.Errorf("%w: %w", errSignInRejected, err)
	}
	if idToken.Subject == "" {
		return tsAuthIdentity{}, "", time.Time{}, fmt.Errorf("%w: ID token has no subject", errSignInRejected)
	}
	email := core.NormalizeEmail(claims.Email)
	if !claims.EmailVerified || len(email) > 254 || !validEmail(email) {
		return tsAuthIdentity{}, "", time.Time{}, errEmailUnusable
	}
	name := strings.TrimSpace(claims.Name)
	if name == "" || !utf8.ValidString(name) || strings.ContainsFunc(name, unicode.IsControl) || utf8.RuneCountInString(name) > 128 {
		name = email
	}
	refresh, err := encodeRefresh(idToken.Subject, token)
	if err != nil {
		return tsAuthIdentity{}, "", time.Time{}, err
	}
	return tsAuthIdentity{Subject: idToken.Subject, Email: email, Name: name}, refresh, recheckAt(token), nil
}

// tsAuthAccount returns the account a ts-auth user signs in to: the one
// linked to their subject; else the one registered with their verified
// email, which it links; else a new one.
func tsAuthAccount(ctx context.Context, store core.Store, identity tsAuthIdentity) (core.Account, error) {
	account, err := store.FindBySubject(ctx, identity.Subject)
	if !errors.Is(err, core.ErrNotFound) {
		return account, err
	}
	account, err = store.FindByEmail(ctx, identity.Email)
	if errors.Is(err, core.ErrNotFound) {
		// The account has no password; ts-auth holds the user's credentials.
		passwordHash, err := HashPassword(oauth2.GenerateVerifier())
		if err != nil {
			return core.Account{}, err
		}
		return store.CreateAccount(ctx, core.User{
			Email: identity.Email, Name: identity.Name, AuthSubject: identity.Subject,
			PasswordHash: passwordHash, Locale: "en", Role: "owner",
		})
	}
	if err != nil {
		return core.Account{}, err
	}
	err = store.Update(ctx, account.ID, func(a *core.Account) error {
		if a.User.AuthSubject != "" && a.User.AuthSubject != identity.Subject {
			return errEmailUnusable
		}
		a.User.AuthSubject = identity.Subject
		return nil
	})
	if err != nil {
		return core.Account{}, err
	}
	return store.FindBySubject(ctx, identity.Subject)
}

// RefreshSession is the session manager's Refresh hook. It redeems the
// session's refresh token and checks that a new ID token, if any, still
// names the session's subject. A refused token ends the session.
func (t *TSAuth) RefreshSession(ctx context.Context, refresh string) (string, time.Time, error) {
	var state refreshState
	if json.Unmarshal([]byte(refresh), &state) != nil || state.Token == "" {
		return "", time.Time{}, fmt.Errorf("unreadable ts-auth refresh state: %w", core.ErrUnauthorized)
	}
	provider, err := t.discover(ctx)
	if err != nil {
		return "", time.Time{}, err
	}
	ctx = oidc.ClientContext(ctx, t.client)
	token, err := provider.oauth.TokenSource(ctx, &oauth2.Token{RefreshToken: state.Token}).Token()
	if refused, ok := errors.AsType[*oauth2.RetrieveError](err); ok && refused.ErrorCode == "invalid_grant" {
		return "", time.Time{}, fmt.Errorf("ts-auth refused the refresh token: %w", core.ErrUnauthorized)
	}
	if err != nil {
		return "", time.Time{}, err
	}
	if raw, _ := token.Extra("id_token").(string); raw != "" {
		idToken, err := provider.verifier.Verify(ctx, raw)
		if err != nil {
			return "", time.Time{}, fmt.Errorf("refreshed ID token: %w: %w", err, core.ErrUnauthorized)
		}
		if idToken.Subject != state.Subject {
			return "", time.Time{}, fmt.Errorf("refreshed ID token names another subject: %w", core.ErrUnauthorized)
		}
	}
	next, err := encodeRefresh(state.Subject, token)
	return next, recheckAt(token), err
}

// RevokeSession is the session manager's Revoke hook: signing out of the
// dashboard ends the ts-auth sign-in behind the session, so its refresh
// token stops working too.
func (t *TSAuth) RevokeSession(ctx context.Context, refresh string) error {
	var state refreshState
	if json.Unmarshal([]byte(refresh), &state) != nil || state.Token == "" {
		return nil
	}
	return t.revoke(ctx, state.Token)
}

func encodeRefresh(subject string, token *oauth2.Token) (string, error) {
	if token.RefreshToken == "" {
		// ts-auth issued no refresh token: the session is not rechecked.
		return "", nil
	}
	value, err := json.Marshal(refreshState{Subject: subject, Token: token.RefreshToken})
	return string(value), err
}

// recheckAt is when to recheck a session with ts-auth: when the access token
// it issued expires, but not before minRecheck.
func recheckAt(token *oauth2.Token) time.Time {
	if earliest := time.Now().Add(minRecheck); token.Expiry.Before(earliest) {
		return earliest
	}
	return token.Expiry
}

// signInAgain is the sign-in page asking ts-auth for credentials even if the
// browser is still signed in to it.
const signInAgain = "/login?prompt=login"

// endSessionURL is where signing out sends the browser: ts-auth's
// end_session_endpoint, which returns it to the sign-in page, or signInAgain
// when ts-auth has none or cannot be reached.
func (t *TSAuth) endSessionURL(ctx context.Context) string {
	provider, err := t.discover(ctx)
	if err != nil || provider.endSession == "" {
		return signInAgain
	}
	target, err := url.Parse(provider.endSession)
	signInPage, signInErr := url.Parse(t.cfg.RedirectURL)
	if err != nil || signInErr != nil {
		return signInAgain
	}
	signInPage.Path = "/login"
	query := target.Query()
	query.Set("client_id", t.cfg.ClientID)
	query.Set("post_logout_redirect_uri", signInPage.String())
	target.RawQuery = query.Encode()
	return target.String()
}

// signInFailed logs why a sign-in failed and shows the visitor how to
// recover, with the reference that finds the log line. The
// recovery's ways back continue to returnTo, the page the sign-in was for.
func signInFailed(w http.ResponseWriter, r *http.Request, deps core.Deps, status int, message, returnTo string, err error) {
	renderAuthStatus(w, r, deps, authStatusPageData{
		Title: "Sign in", Description: "Sign-in through ts-auth did not finish.", Message: message, Status: status,
		Reference: logFailure("ts-auth sign-in", err), Recovery: true, ReturnTo: SafeRedirectTarget(returnTo),
	})
}

// logFailure logs err under a new short reference a visitor can quote, so
// support finds the log line, and returns the reference.
func logFailure(event string, err error) string {
	ref := strings.ToLower(rand.Text()[:10])
	slog.Warn(event, "error", err, "ref", ref)
	return ref
}

// withReference is message followed by the reference of its log line.
func withReference(message, ref string) string {
	return message + " Reference: " + ref + "."
}
