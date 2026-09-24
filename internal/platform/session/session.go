package session

import (
	"crypto/rand"
	"crypto/sha256"
	"encoding/base64"
	"net/http"
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
	a, err := m.store.SnapshotAs(p)
	if err != nil {
		return err
	}
	if checkHash && (expectedHash == "" || a.User.PasswordHash != expectedHash) {
		return core.ErrConflict
	}
	var random [32]byte
	if _, err := rand.Read(random[:]); err != nil {
		return err
	}
	token := base64.RawURLEncoding.EncodeToString(random[:])
	m.mu.Lock()
	now := time.Now()
	for key, item := range m.items {
		if !now.Before(item.expiresAt) {
			delete(m.items, key)
		}
	}
	if old, ok := m.Token(r); ok {
		delete(m.items, sha256.Sum256([]byte(old)))
	}
	m.items[sha256.Sum256([]byte(token))] = entry{
		accountID: p.AccountID, userID: p.UserID, authVersion: a.AuthVersion, expiresAt: now.Add(m.opts.Lifetime),
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
	m.mu.RLock()
	item, ok := m.items[sha256.Sum256([]byte(token))]
	m.mu.RUnlock()
	if !ok {
		return core.Principal{}, false
	}
	if !time.Now().Before(item.expiresAt) {
		m.mu.Lock()
		delete(m.items, sha256.Sum256([]byte(token)))
		m.mu.Unlock()
		return core.Principal{}, false
	}
	a, ok := m.store.Snapshot(item.accountID)
	if !ok || a.User.ID != item.userID || a.AuthVersion != item.authVersion {
		return core.Principal{}, false
	}
	p := a.Principal()
	current, err := m.store.SnapshotAs(p)
	if err != nil || current.AuthVersion != item.authVersion {
		return core.Principal{}, false
	}
	return p, true
}

func (m *Manager) End(w http.ResponseWriter, r *http.Request) {
	if scheme, bearer, ok := strings.Cut(r.Header.Get("Authorization"), " "); ok && strings.EqualFold(scheme, "Bearer") {
		if _, valid := m.principalFor(bearer); valid {
			m.mu.Lock()
			delete(m.items, sha256.Sum256([]byte(bearer)))
			m.mu.Unlock()
		}
	}
	if token, ok := m.Token(r); ok {
		m.mu.Lock()
		delete(m.items, sha256.Sum256([]byte(token)))
		m.mu.Unlock()
	}
	http.SetCookie(w, &http.Cookie{
		Name: m.opts.CookieName, Path: "/", HttpOnly: true, Secure: m.opts.Secure,
		SameSite: http.SameSiteLaxMode, MaxAge: -1,
	})
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
