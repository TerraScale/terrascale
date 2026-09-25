package session

import (
	"crypto/rand"
	"crypto/sha256"
	"encoding/base64"
	"encoding/hex"
	"maps"
	"net/http"
	"slices"
	"strings"
	"sync"
	"time"

	"terrascale.tech/dashboard/internal/core"
)

type Options struct {
	CookieName string
	Secure     bool
	Lifetime   time.Duration
}

type entry struct {
	accountID   string
	userID      string
	authVersion uint64
	expiresAt   time.Time
}

type Manager struct {
	store *core.Store
	opts  Options
	mu    sync.RWMutex
	items map[[32]byte]entry
}

func New(store *core.Store, opts Options) *Manager {
	if opts.CookieName == "" {
		opts.CookieName = "ts_dashboard_session"
	}
	if opts.Lifetime <= 0 {
		opts.Lifetime = 12 * time.Hour
	}
	return &Manager{store: store, opts: opts, items: make(map[[32]byte]entry)}
}

func (m *Manager) Start(w http.ResponseWriter, r *http.Request, p core.Principal) error {
	return m.start(w, r, p, "", false)
}

func (m *Manager) StartIfPasswordHash(w http.ResponseWriter, r *http.Request, p core.Principal, expectedHash string) error {
	return m.start(w, r, p, expectedHash, true)
}

func (m *Manager) start(w http.ResponseWriter, r *http.Request, p core.Principal, expectedHash string, checkHash bool) error {
	var authVersion uint64
	if err := m.store.ViewAs(p, func(a core.Account) error {
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
	old, hasOld := m.Token(r)
	m.mu.Lock()
	maps.DeleteFunc(m.items, func(_ [32]byte, item entry) bool { return !now.Before(item.expiresAt) })
	if hasOld {
		delete(m.items, sha256.Sum256([]byte(old)))
	}
	m.items[key] = entry{
		accountID: p.AccountID, userID: p.UserID, authVersion: authVersion, expiresAt: now.Add(m.opts.Lifetime),
	}
	m.mu.Unlock()
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
	return m.principalFor(token)
}

func (m *Manager) BearerPrincipal(r *http.Request) (core.Principal, bool) {
	scheme, token, ok := strings.Cut(r.Header.Get("Authorization"), " ")
	if !ok || !strings.EqualFold(scheme, "Bearer") || token == "" || strings.ContainsAny(token, " \t\r\n") {
		return core.Principal{}, false
	}
	return m.principalFor(token)
}

func (m *Manager) Token(r *http.Request) (string, bool) {
	cookie, err := r.Cookie(m.opts.CookieName)
	if err != nil || cookie.Value == "" {
		return "", false
	}
	return cookie.Value, true
}

func (m *Manager) principalFor(token string) (core.Principal, bool) {
	key := sha256.Sum256([]byte(token))
	m.mu.RLock()
	item, ok := m.items[key]
	m.mu.RUnlock()
	if !ok {
		return core.Principal{}, false
	}
	if !time.Now().Before(item.expiresAt) {
		m.mu.Lock()
		delete(m.items, key)
		m.mu.Unlock()
		return core.Principal{}, false
	}
	return m.store.SessionPrincipal(item.accountID, item.userID, item.authVersion)
}

func (m *Manager) End(w http.ResponseWriter, r *http.Request) {
	if scheme, bearer, ok := strings.Cut(r.Header.Get("Authorization"), " "); ok && strings.EqualFold(scheme, "Bearer") {
		if _, valid := m.principalFor(bearer); valid {
			m.forget(bearer)
		}
	}
	if token, ok := m.Token(r); ok {
		m.forget(token)
	}
	http.SetCookie(w, &http.Cookie{
		Name: m.opts.CookieName, Path: "/", HttpOnly: true, Secure: m.opts.Secure,
		SameSite: http.SameSiteLaxMode, MaxAge: -1,
	})
}

func (m *Manager) forget(token string) {
	key := sha256.Sum256([]byte(token))
	m.mu.Lock()
	delete(m.items, key)
	m.mu.Unlock()
}

func (m *Manager) RevokeUser(accountID, userID string) {
	m.mu.Lock()
	defer m.mu.Unlock()
	for token, item := range m.items {
		if item.accountID == accountID && item.userID == userID {
			delete(m.items, token)
		}
	}
}

// SessionsForUser exposes only this principal's live local sessions. The ID is
// an opaque display identifier derived from the stored token digest.
func (m *Manager) SessionsForUser(p core.Principal, r *http.Request) []core.SessionInfo {
	currentToken, hasCurrent := m.Token(r)
	var currentHash [32]byte
	if hasCurrent {
		currentHash = sha256.Sum256([]byte(currentToken))
	}
	now := time.Now()
	var result []core.SessionInfo
	m.mu.RLock()
	for hash, item := range m.items {
		if item.accountID == p.AccountID && item.userID == p.UserID && item.authVersion == p.AuthVersion && now.Before(item.expiresAt) {
			result = append(result, core.SessionInfo{
				ID: hex.EncodeToString(hash[:6]), Current: hasCurrent && hash == currentHash,
				ExpiresAt: item.expiresAt,
			})
		}
	}
	m.mu.RUnlock()
	slices.SortFunc(result, func(a, b core.SessionInfo) int {
		return b.ExpiresAt.Compare(a.ExpiresAt)
	})
	return result
}
