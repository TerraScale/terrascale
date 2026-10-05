package session

import (
	"context"
	"crypto/rand"
	"crypto/sha256"
	"encoding/base64"
	"encoding/hex"
	"errors"
	"log/slog"
	"net/http"
	"slices"
	"strings"
	"time"

	"terrascale.tech/dashboard/internal/core"
)

type Options struct {
	CookieName string
	Secure     bool
	Lifetime   time.Duration
	// Store keeps the sessions; nil keeps them in this process's memory.
	Store Store
}

type Manager struct {
	store    core.Store
	sessions Store
	opts     Options
}

// New returns a session manager for the accounts in store. Sessions live in
// opts.Store, or in this process's memory when it is nil.
func New(store core.Store, opts Options) *Manager {
	if opts.CookieName == "" {
		opts.CookieName = "ts_dashboard_session"
	}
	if opts.Lifetime <= 0 {
		opts.Lifetime = 12 * time.Hour
	}
	sessions := opts.Store
	if sessions == nil {
		sessions = NewMemoryStore()
	}
	return &Manager{store: store, sessions: sessions, opts: opts}
}

func (m *Manager) Start(w http.ResponseWriter, r *http.Request, p core.Principal) error {
	return m.start(w, r, p, "", false)
}

func (m *Manager) StartIfPasswordHash(w http.ResponseWriter, r *http.Request, p core.Principal, expectedHash string) error {
	return m.start(w, r, p, expectedHash, true)
}

func (m *Manager) start(w http.ResponseWriter, r *http.Request, p core.Principal, expectedHash string, checkHash bool) error {
	ctx := r.Context()
	var authVersion uint64
	if err := m.store.ViewAs(ctx, p, func(a core.Account) error {
		if checkHash && (expectedHash == "" || a.User.PasswordHash != expectedHash) {
			return core.ErrConflict
		}
		authVersion = a.AuthVersion
		return nil
	}); err != nil {
		return err
	}
	var random [32]byte
	rand.Read(random[:])
	token := base64.RawURLEncoding.EncodeToString(random[:])
	now := time.Now()
	key := sha256.Sum256([]byte(token))
	// The new session is saved before the old one is deleted: if saving
	// fails the caller stays signed in, and an old record a store failed to
	// delete still expires.
	if err := m.sessions.Put(ctx, key, Record{
		AccountID: p.AccountID, UserID: p.UserID, AuthVersion: authVersion, ExpiresAt: now.Add(m.opts.Lifetime),
	}); err != nil {
		return err
	}
	if old, hasOld := m.Token(r); hasOld {
		if err := m.sessions.Delete(ctx, sha256.Sum256([]byte(old))); err != nil {
			slog.Warn("delete replaced session", "error", err)
		}
	}
	http.SetCookie(w, &http.Cookie{
		Name: m.opts.CookieName, Value: token, Path: "/", HttpOnly: true,
		Secure: m.opts.Secure, SameSite: http.SameSiteLaxMode,
		MaxAge: int(m.opts.Lifetime.Seconds()),
	})
	return nil
}

func (m *Manager) Principal(r *http.Request) (core.Principal, bool) {
	token, ok := m.Token(r)
	if !ok {
		return core.Principal{}, false
	}
	return m.principalFor(r.Context(), token)
}

func (m *Manager) BearerPrincipal(r *http.Request) (core.Principal, bool) {
	scheme, token, ok := strings.Cut(r.Header.Get("Authorization"), " ")
	if !ok || !strings.EqualFold(scheme, "Bearer") || token == "" || strings.ContainsAny(token, " \t\r\n") {
		return core.Principal{}, false
	}
	return m.principalFor(r.Context(), token)
}

func (m *Manager) Token(r *http.Request) (string, bool) {
	cookie, err := r.Cookie(m.opts.CookieName)
	if err != nil || cookie.Value == "" {
		return "", false
	}
	return cookie.Value, true
}

func (m *Manager) principalFor(ctx context.Context, token string) (core.Principal, bool) {
	key := sha256.Sum256([]byte(token))
	p, valid, err := m.principalForHash(ctx, key)
	if err != nil {
		slog.Warn("resolve session", "error", err)
	}
	return p, valid
}

// BindCLIGrant binds a device approval to the request's live session by the
// session's token digest, which a grant can store without the token itself.
func (m *Manager) BindCLIGrant(r *http.Request) (string, bool) {
	token, ok := m.Token(r)
	if !ok {
		return "", false
	}
	key := sha256.Sum256([]byte(token))
	if _, valid, err := m.principalForHash(r.Context(), key); !valid || err != nil {
		return "", false
	}
	return hex.EncodeToString(key[:]), true
}

// CLIGrantPrincipal resolves a BindCLIGrant binding while its session lives.
func (m *Manager) CLIGrantPrincipal(ctx context.Context, binding string) (core.Principal, bool, error) {
	var key [32]byte
	if len(binding) != hex.EncodedLen(len(key)) {
		return core.Principal{}, false, nil
	}
	if _, err := hex.Decode(key[:], []byte(binding)); err != nil {
		return core.Principal{}, false, nil
	}
	return m.principalForHash(ctx, key)
}

// principalForHash distinguishes invalid credentials from a failed lookup.
func (m *Manager) principalForHash(ctx context.Context, key [32]byte) (core.Principal, bool, error) {
	item, err := m.sessions.Get(ctx, key, time.Now())
	if errors.Is(err, ErrNotFound) {
		return core.Principal{}, false, nil
	}
	if err != nil {
		return core.Principal{}, false, err
	}
	p, err := m.store.SessionPrincipal(ctx, item.AccountID, item.UserID, item.AuthVersion)
	if errors.Is(err, core.ErrUnauthorized) || errors.Is(err, core.ErrForbidden) || errors.Is(err, core.ErrNotFound) {
		return core.Principal{}, false, nil
	}
	if err != nil {
		return core.Principal{}, false, err
	}
	return p, true, nil
}

func (m *Manager) End(w http.ResponseWriter, r *http.Request) error {
	token, _ := m.Token(r)
	var err error
	if token != "" {
		err = m.sessions.Delete(r.Context(), sha256.Sum256([]byte(token)))
	}
	if scheme, bearer, ok := strings.Cut(r.Header.Get("Authorization"), " "); ok && strings.EqualFold(scheme, "Bearer") && bearer != "" && !strings.ContainsAny(bearer, " \t\r\n") && bearer != token {
		// Revoke the supplied token without a lookup: an account outage must
		// not prevent logout. Try both tokens even if one deletion fails.
		err = errors.Join(err, m.sessions.Delete(r.Context(), sha256.Sum256([]byte(bearer))))
	}
	if err != nil {
		return err
	}
	http.SetCookie(w, &http.Cookie{
		Name: m.opts.CookieName, Path: "/", HttpOnly: true, Secure: m.opts.Secure,
		SameSite: http.SameSiteLaxMode, MaxAge: -1,
	})
	return nil
}

func (m *Manager) RevokeUser(ctx context.Context, accountID, userID string) error {
	return m.sessions.DeleteUser(ctx, accountID, userID)
}

// SessionsForUser exposes only this principal's live local sessions. The ID is
// an opaque display identifier derived from the stored token digest.
func (m *Manager) SessionsForUser(p core.Principal, r *http.Request) []core.SessionInfo {
	currentToken, hasCurrent := m.Token(r)
	var currentHash [32]byte
	if hasCurrent {
		currentHash = sha256.Sum256([]byte(currentToken))
	}
	entries, err := m.sessions.ListUser(r.Context(), p.AccountID, p.UserID, time.Now())
	if err != nil {
		// The signature cannot report the failure; log it so an empty list
		// from a failing store is not mistaken for no other sessions.
		slog.Warn("list user sessions", "error", err)
		return nil
	}
	var result []core.SessionInfo
	for _, item := range entries {
		if item.AuthVersion == p.AuthVersion {
			result = append(result, core.SessionInfo{
				ID: hex.EncodeToString(item.Key[:6]), Current: hasCurrent && item.Key == currentHash,
				ExpiresAt: item.ExpiresAt,
			})
		}
	}
	slices.SortFunc(result, func(a, b core.SessionInfo) int {
		return b.ExpiresAt.Compare(a.ExpiresAt)
	})
	return result
}
