package main

import (
	"context"
	"crypto/rand"
	"crypto/subtle"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"html/template"
	"net/http"
	"sync"
	"time"

	sdk "terrascale.tech/sdk"
)

type identityClient interface {
	BeginAuthorization(context.Context, []string) (*sdk.TSAuthAuthorizationAttempt, error)
	ExchangeCode(context.Context, sdk.TSAuthOIDCCallback, sdk.TSAuthAuthorizationAttempt) (*sdk.TSAuthOIDCLogin, error)
	Introspect(context.Context, string) (*sdk.TSAuthIntrospection, error)
	Revoke(context.Context, string) error
	EndSessionURL(context.Context, string, string) (string, error)
}
type documentClient interface {
	Get(context.Context, sdk.DocumentScope, sdk.UUID, sdk.UUID) (*sdk.DocumentPoint, error)
	Commit(context.Context, sdk.DocumentScope, sdk.CommitRequest) (*sdk.CommitResponse, error)
	TransactionStatus(context.Context, sdk.DocumentScope, sdk.TransactionStatusRequest) (*sdk.TransactionStatusResponse, error)
}
type authorization struct {
	attempt sdk.TSAuthAuthorizationAttempt
}
type operation struct {
	document sdk.UUID
	request  sdk.CommitRequest
}
type browserSession struct {
	mu       sync.Mutex
	identity sdk.TSAuthVerifiedIDToken
	tokens   sdk.TSAuthTokenResponse
	csrf     string
	expires  time.Time
	pending  *operation
	closing  bool
}
type application struct {
	config    configuration
	oidc      identityClient
	documents documentClient
	mu        sync.Mutex
	attempts  map[string]authorization
	sessions  map[string]*browserSession
}

func newApplication(c configuration, oidc identityClient, documents documentClient) *application {
	return &application{config: c, oidc: oidc, documents: documents, attempts: make(map[string]authorization), sessions: make(map[string]*browserSession)}
}
func (a *application) handler() http.Handler {
	mux := http.NewServeMux()
	mux.HandleFunc("GET /{$}", a.home)
	mux.HandleFunc("GET /sign-in", a.signIn)
	mux.HandleFunc("GET /callback", a.callback)
	mux.HandleFunc("POST /notes", a.create)
	mux.HandleFunc("GET /notes/{id}", a.read)
	mux.HandleFunc("POST /notes/{id}", a.update)
	mux.HandleFunc("GET /operation", a.operationStatus)
	mux.HandleFunc("POST /logout", a.logout)
	mux.HandleFunc("GET /signed-out", a.signedOut)
	mux.HandleFunc("GET /query", a.unsupported)
	mux.HandleFunc("GET /subscription", a.unsupported)
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Cache-Control", "no-store")
		w.Header().Set("Referrer-Policy", "no-referrer")
		w.Header().Set("X-Content-Type-Options", "nosniff")
		w.Header().Set("Content-Security-Policy", "default-src 'none'; form-action 'self'; base-uri 'none'; frame-ancestors 'none'")
		mux.ServeHTTP(w, r)
	})
}
func randomToken() (string, error) {
	var b [32]byte
	if _, err := rand.Read(b[:]); err != nil {
		return "", err
	}
	return base64.RawURLEncoding.EncodeToString(b[:]), nil
}
func newUUID() (sdk.UUID, error) {
	var b [16]byte
	if _, err := rand.Read(b[:]); err != nil {
		return "", err
	}
	b[6] = b[6]&0x0f | 0x40
	b[8] = b[8]&0x3f | 0x80
	return sdk.ParseUUID(fmt.Sprintf("%x-%x-%x-%x-%x", b[:4], b[4:6], b[6:8], b[8:10], b[10:]))
}
func (a *application) cookie(w http.ResponseWriter, name, value string, maxAge int) {
	prefix := "__Host-tsref-"
	if a.config.development {
		prefix = "tsref-dev-"
	}
	http.SetCookie(w, &http.Cookie{Name: prefix + name, Value: value, Path: "/", Secure: !a.config.development, HttpOnly: true, SameSite: http.SameSiteLaxMode, MaxAge: maxAge})
}
func (a *application) cookieValue(r *http.Request, name string) string {
	prefix := "__Host-tsref-"
	if a.config.development {
		prefix = "tsref-dev-"
	}
	c, err := r.Cookie(prefix + name)
	if err != nil {
		return ""
	}
	return c.Value
}
func (a *application) pruneLocked() {
	now := time.Now()
	for id, attempt := range a.attempts {
		if !now.Before(attempt.attempt.ExpiresAt) {
			delete(a.attempts, id)
		}
	}
	for id, session := range a.sessions {
		if !now.Before(session.expires) {
			delete(a.sessions, id)
		}
	}
}
func (a *application) signIn(w http.ResponseWriter, r *http.Request) {
	attempt, err := a.oidc.BeginAuthorization(r.Context(), []string{"openid", "email", "profile"})
	if err != nil {
		safeError(w, 503, "identity_unavailable", "Sign-in is unavailable. Retry when the identity service is ready.")
		return
	}
	id, err := randomToken()
	if err != nil {
		safeError(w, 503, "random_unavailable", "Sign-in is unavailable.")
		return
	}
	a.mu.Lock()
	a.pruneLocked()
	if len(a.attempts) >= 1024 {
		a.mu.Unlock()
		safeError(w, 503, "capacity_unavailable", "Sign-in is temporarily unavailable.")
		return
	}
	a.attempts[id] = authorization{*attempt}
	a.mu.Unlock()
	a.cookie(w, "login", id, 300)
	http.Redirect(w, r, attempt.AuthorizationURL, http.StatusSeeOther)
}
func (a *application) callback(w http.ResponseWriter, r *http.Request) {
	id := a.cookieValue(r, "login")
	a.mu.Lock()
	attempt, ok := a.attempts[id]
	delete(a.attempts, id)
	a.mu.Unlock()
	a.cookie(w, "login", "", -1)
	if !ok || r.URL.Query().Get("error") != "" {
		safeError(w, 400, "invalid_callback", "Sign-in attempt is invalid or expired. Start sign-in again.")
		return
	}
	login, err := a.oidc.ExchangeCode(r.Context(), sdk.TSAuthOIDCCallback{Code: r.URL.Query().Get("code"), State: r.URL.Query().Get("state")}, attempt.attempt)
	if err != nil || login == nil || login.Identity.Issuer != a.config.issuer || login.Identity.Subject == "" {
		safeError(w, 401, "sign_in_failed", "Sign-in could not be verified. Start sign-in again.")
		return
	}
	id, err = randomToken()
	if err != nil {
		safeError(w, 503, "random_unavailable", "Sign-in is unavailable.")
		return
	}
	csrf, err := randomToken()
	if err != nil {
		safeError(w, 503, "random_unavailable", "Sign-in is unavailable.")
		return
	}
	expires := time.Now().Add(time.Duration(login.Tokens.ExpiresIn) * time.Second)
	if login.Identity.ExpiresAt.Before(expires) {
		expires = login.Identity.ExpiresAt
	}
	if !time.Now().Before(expires) {
		safeError(w, 401, "expired_identity", "Sign-in expired. Start sign-in again.")
		return
	}
	session := &browserSession{identity: login.Identity, tokens: login.Tokens, csrf: csrf, expires: expires}
	a.mu.Lock()
	a.pruneLocked()
	if len(a.sessions) >= 1024 {
		a.mu.Unlock()
		safeError(w, 503, "capacity_unavailable", "Sign-in is temporarily unavailable.")
		return
	}
	a.sessions[id] = session
	a.mu.Unlock()
	a.cookie(w, "session", id, max(1, int(time.Until(expires).Seconds())))
	http.Redirect(w, r, "/", http.StatusSeeOther)
}
func (a *application) lookup(r *http.Request) *browserSession {
	a.mu.Lock()
	defer a.mu.Unlock()
	a.pruneLocked()
	return a.sessions[a.cookieValue(r, "session")]
}
func (a *application) authenticated(w http.ResponseWriter, r *http.Request) *browserSession {
	s := a.lookup(r)
	if s == nil {
		safeError(w, 401, "sign_in_required", "Sign in to continue.")
		return nil
	}
	s.mu.Lock()
	closing := s.closing
	s.mu.Unlock()
	if closing {
		safeError(w, 401, "sign_out_pending", "Sign-out is pending. Retry sign-out to finish revocation.")
		return nil
	}
	status, err := a.oidc.Introspect(r.Context(), s.tokens.AccessToken)
	if err != nil {
		safeError(w, 503, "identity_unavailable", "Session verification is unavailable. Retry when the identity service is ready.")
		return nil
	}
	if status == nil || !status.Active || status.Sub == nil || *status.Sub != s.identity.Subject {
		safeError(w, 401, "session_inactive", "The session is inactive. Sign in again.")
		return nil
	}
	return s
}
func (a *application) form(w http.ResponseWriter, r *http.Request, s *browserSession) bool {
	r.Body = http.MaxBytesReader(w, r.Body, 8<<10)
	if r.Header.Get("Origin") != a.config.origin || r.ParseForm() != nil || subtle.ConstantTimeCompare([]byte(r.PostForm.Get("csrf")), []byte(s.csrf)) != 1 {
		safeError(w, 403, "request_rejected", "The request could not be verified. Reload and retry.")
		return false
	}
	return true
}
func (a *application) owner(s *browserSession) string {
	return s.identity.Issuer + "\x00" + s.identity.Subject
}
func (a *application) owned(point *sdk.DocumentPoint, s *browserSession) (string, bool) {
	if point == nil || point.State != sdk.PointValue || point.StateVersion == nil || len(point.Siblings) != 1 || point.Siblings[0].Kind != "live" {
		return "", false
	}
	var owner, body string
	var ownerFound, bodyFound bool
	for _, f := range point.Siblings[0].Fields {
		switch f.ID {
		case a.config.ownerField:
			owner, ownerFound = f.Value.UTF8()
		case a.config.bodyField:
			body, bodyFound = f.Value.UTF8()
		}
	}
	return body, ownerFound && bodyFound && owner == a.owner(s)
}
func (a *application) home(w http.ResponseWriter, r *http.Request) {
	session := a.lookup(r)
	if session == nil {
		render(w, pageData{Message: "Sign in to create and read your notes."})
		return
	}
	session.mu.Lock()
	closing := session.closing
	session.mu.Unlock()
	if closing {
		render(w, pageData{SignedIn: true, Closing: true, CSRF: session.csrf, Message: "Data access is closed. Retry sign-out to finish provider revocation."})
		return
	}
	s := a.authenticated(w, r)
	if s == nil {
		return
	}
	render(w, pageData{SignedIn: true, CSRF: s.csrf})
}
func (a *application) create(w http.ResponseWriter, r *http.Request) {
	s := a.authenticated(w, r)
	if s == nil || !a.form(w, r, s) {
		return
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.closing {
		safeError(w, 401, "sign_out_pending", "Sign-out is pending.")
		return
	}
	if s.pending != nil {
		safeError(w, 409, "transaction_unresolved", "A previous write is unresolved. Check /operation before starting another write.")
		return
	}
	id, err := newUUID()
	if err != nil {
		safeError(w, 503, "random_unavailable", "The write could not be prepared.")
		return
	}
	a.write(w, r, s, id, nil, sdk.DocumentCondition{Kind: sdk.ConditionAbsent})
}
func (a *application) read(w http.ResponseWriter, r *http.Request) {
	s := a.authenticated(w, r)
	if s == nil {
		return
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.closing {
		safeError(w, 401, "sign_out_pending", "Sign-out is pending.")
		return
	}
	id, err := sdk.ParseUUID(r.PathValue("id"))
	if err != nil {
		notFound(w)
		return
	}
	point, err := a.documents.Get(r.Context(), a.config.scope, a.config.collection, id)
	if err != nil {
		safeError(w, 503, "database_unavailable", "The scoped read is unavailable. Check the connection diagnosis.")
		return
	}
	body, ok := a.owned(point, s)
	if !ok {
		notFound(w)
		return
	}
	render(w, pageData{SignedIn: true, CSRF: s.csrf, ID: string(id), Body: body, Version: point.StateVersion.String()})
}
func (a *application) update(w http.ResponseWriter, r *http.Request) {
	s := a.authenticated(w, r)
	if s == nil || !a.form(w, r, s) {
		return
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.closing {
		safeError(w, 401, "sign_out_pending", "Sign-out is pending.")
		return
	}
	if s.pending != nil {
		safeError(w, 409, "transaction_unresolved", "A previous write is unresolved. Check /operation before starting another write.")
		return
	}
	id, err := sdk.ParseUUID(r.PathValue("id"))
	if err != nil {
		notFound(w)
		return
	}
	point, err := a.documents.Get(r.Context(), a.config.scope, a.config.collection, id)
	if err != nil {
		safeError(w, 503, "database_unavailable", "The scoped read is unavailable.")
		return
	}
	if _, ok := a.owned(point, s); !ok {
		notFound(w)
		return
	}
	if point.StateVersion.String() != r.PostForm.Get("version") {
		safeError(w, 409, "record_changed", "The note changed. Reload before updating it.")
		return
	}
	a.write(w, r, s, id, point.Context, sdk.DocumentCondition{Kind: sdk.ConditionVersion, Version: point.StateVersion})
}
func (a *application) write(w http.ResponseWriter, r *http.Request, s *browserSession, id sdk.UUID, observed []sdk.Observed, condition sdk.DocumentCondition) {
	body := r.PostForm.Get("body")
	if len(body) == 0 || len(body) > 4096 {
		safeError(w, 400, "invalid_note", "Enter a note between 1 and 4096 UTF-8 bytes.")
		return
	}
	ownerValue, err := sdk.UTF8Value(a.owner(s))
	if err != nil {
		safeError(w, 400, "invalid_identity", "The user identity cannot be stored.")
		return
	}
	bodyValue, err := sdk.UTF8Value(body)
	if err != nil {
		safeError(w, 400, "invalid_note", "Enter valid UTF-8 text.")
		return
	}
	tx, err := sdk.NewTransactionID(uint64(time.Now().Add(30 * time.Second).UnixMilli()))
	if err != nil {
		safeError(w, 503, "random_unavailable", "The write could not be prepared.")
		return
	}
	request, err := sdk.NewCommitRequest(a.config.scope, tx, sdk.MutationAuthority{Mode: sdk.AuthorityLeaderless}, []sdk.DocumentWrite{{
		Collection: a.config.collection, Document: id, SchemaGeneration: a.config.schemaGeneration, SchemaDigest: a.config.schemaDigest, Condition: condition, Observed: observed,
		Value: sdk.WriteValue{Kind: "live", Fields: []sdk.DocumentField{{ID: a.config.ownerField, Value: ownerValue}, {ID: a.config.bodyField, Value: bodyValue}}},
	}})
	if err != nil {
		safeError(w, 400, "invalid_write", "The write does not match the required schema.")
		return
	}
	// Retain the exact identity before dispatch. An uncertain outcome never
	// authorizes a fresh identity, replay, or declaring the write aborted.
	s.pending = &operation{document: id, request: request}
	_, err = a.documents.Commit(r.Context(), a.config.scope, request)
	if err != nil {
		var commit *sdk.CommitError
		if errors.As(err, &commit) && commit.Uncertain {
			safeError(w, 503, "transaction_unresolved", "The write outcome is unresolved. Check /operation; do not repeat it.")
			return
		}
		s.pending = nil
		var apiErr *sdk.APIError
		if commit != nil && errors.As(commit.Err, &apiErr) && apiErr.Status == http.StatusPreconditionFailed && apiErr.Code == "precondition_failed" {
			safeError(w, 409, "record_changed", "The note changed. Reload before updating it.")
			return
		}
		safeError(w, 503, "database_capability_unavailable", "The scoped write is unavailable. Check SDK discovery, credential scope and schema prerequisites.")
		return
	}
	s.pending = nil
	http.Redirect(w, r, "/notes/"+string(id), http.StatusSeeOther)
}
func (a *application) operationStatus(w http.ResponseWriter, r *http.Request) {
	s := a.authenticated(w, r)
	if s == nil {
		return
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.closing {
		safeError(w, 401, "sign_out_pending", "Sign-out is pending.")
		return
	}
	if s.pending == nil {
		safeError(w, 404, "no_pending_operation", "There is no pending write in this session.")
		return
	}
	status, err := a.documents.TransactionStatus(r.Context(), a.config.scope, s.pending.request.StatusRequest())
	if err != nil || status == nil {
		safeError(w, 503, "status_unavailable", "The original write status is unavailable. Keep the original operation; do not repeat it.")
		return
	}
	if !status.Definitive() {
		safeError(w, 409, "transaction_unresolved", "The write remains unresolved. Retry this status check; do not repeat the write.")
		return
	}
	id := s.pending.document
	s.pending = nil
	if status.State == sdk.TransactionCommitted {
		http.Redirect(w, r, "/notes/"+string(id), http.StatusSeeOther)
		return
	}
	safeError(w, 409, "transaction_rejected", "The original write was rejected. Reload the note before starting a new operation.")
}
func (a *application) logout(w http.ResponseWriter, r *http.Request) {
	s := a.lookup(r)
	if s == nil {
		a.cookie(w, "session", "", -1)
		http.Redirect(w, r, "/", http.StatusSeeOther)
		return
	}
	if !a.form(w, r, s) {
		return
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	s.closing = true
	err := a.oidc.Revoke(r.Context(), s.tokens.AccessToken)
	if err == nil && s.tokens.RefreshToken != nil {
		err = a.oidc.Revoke(r.Context(), *s.tokens.RefreshToken)
	}
	if err != nil {
		w.Header().Set("Content-Type", "text/html; charset=utf-8")
		w.WriteHeader(http.StatusServiceUnavailable)
		render(w, pageData{SignedIn: true, Closing: true, CSRF: s.csrf, Message: "Data access is closed. Provider revocation is incomplete; retry sign-out."})
		return
	}
	a.mu.Lock()
	delete(a.sessions, a.cookieValue(r, "session"))
	a.mu.Unlock()
	a.cookie(w, "session", "", -1)
	state, err := randomToken()
	if err != nil {
		safeError(w, 503, "logout_incomplete", "Local sign-out completed; provider browser sign-out is incomplete.")
		return
	}
	target, err := a.oidc.EndSessionURL(r.Context(), a.config.origin+"/signed-out", state)
	if err != nil {
		safeError(w, 503, "logout_incomplete", "Local sign-out completed; provider browser sign-out is incomplete.")
		return
	}
	a.cookie(w, "logout", state, 300)
	// The form CSP permits only this origin. Complete provider navigation with
	// an ordinary link to the SDK-validated destination, not a form redirect.
	render(w, pageData{LogoutURL: target, Message: "Session tokens revoked. Finish sign-out with your identity provider."})
}
func (a *application) signedOut(w http.ResponseWriter, r *http.Request) {
	state := a.cookieValue(r, "logout")
	a.cookie(w, "logout", "", -1)
	if state == "" || subtle.ConstantTimeCompare([]byte(state), []byte(r.URL.Query().Get("state"))) != 1 {
		safeError(w, 400, "invalid_logout_callback", "Sign-out callback could not be verified.")
		return
	}
	render(w, pageData{Message: "Signed out."})
}
func (a *application) unsupported(w http.ResponseWriter, r *http.Request) {
	if a.authenticated(w, r) == nil {
		return
	}
	safeError(w, 501, "operation_unsupported", "This operation requires an admitted query or subscription contract.")
}
func notFound(w http.ResponseWriter) { safeError(w, 404, "note_not_found", "The note is unavailable.") }
func safeError(w http.ResponseWriter, status int, code, message string) {
	id, err := randomToken()
	if err != nil {
		id = "unavailable"
	}
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(struct {
		Code      string `json:"code"`
		Message   string `json:"message"`
		SupportID string `json:"support_id"`
	}{code, message, id})
}

type pageData struct {
	SignedIn, Closing                           bool
	CSRF, ID, Body, Version, Message, LogoutURL string
}

var page = template.Must(template.New("notes").Parse(`<!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>TerraScale notes</title><main><h1>Your notes</h1><p>{{.Message}}</p>{{if .SignedIn}}{{if not .Closing}}<form method="post" action="/notes"><input type="hidden" name="csrf" value="{{.CSRF}}"><label>New note <textarea name="body" maxlength="4096" required></textarea></label><button>Create note</button></form><p>Open a saved note using its link after creation.</p><p><a href="/query">Query notes</a> · <a href="/subscription">Live updates</a></p>{{if .ID}}<article><p>{{.Body}}</p><form method="post" action="/notes/{{.ID}}"><input type="hidden" name="csrf" value="{{.CSRF}}"><input type="hidden" name="version" value="{{.Version}}"><label>Update note <textarea name="body" maxlength="4096" required>{{.Body}}</textarea></label><button>Save note</button></form></article>{{end}}<p><a href="/operation">Check pending write</a></p>{{end}}<form method="post" action="/logout"><input type="hidden" name="csrf" value="{{.CSRF}}"><button>Sign out and revoke session tokens</button></form>{{else}}{{if .LogoutURL}}<a href="{{.LogoutURL}}">Finish sign-out with identity provider</a>{{else}}<a href="/sign-in">Sign in</a>{{end}}{{end}}</main></html>`))

func render(w http.ResponseWriter, data pageData) {
	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	_ = page.Execute(w, data)
}
