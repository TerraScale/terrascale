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

	"golang.org/x/sync/singleflight"
	"terrascale.tech/dashboard/internal/core"
)

type Options struct {
	CookieName string
	Secure     bool
	Lifetime   time.Duration
	// Store keeps the sessions; nil keeps them in this process's memory.
	Store Store
	// Refresh rechecks a session started with StartRefreshable once its
	// RefreshAt passes, returning the refresh state to keep and when to
	// check again. An error wrapping core.ErrUnauthorized ends the session;
	// any other keeps it and retries a minute later.
	Refresh func(ctx context.Context, refresh string) (string, time.Time, error)
	// Revoke is given the refresh state of a session End ends, so the
	// provider can end its sign-in too. Its failure does not stop sign-out.
	Revoke func(ctx context.Context, refresh string) error
	// UserToken reads the user's ts-auth access token from a session's
	// refresh state. renew asks to recheck the session now to get one, as
	// for a state saved before sessions kept the token.
	UserToken func(refresh string) (token string, renew bool)
}

// refreshTimeout bounds one Options.Refresh call.
const refreshTimeout = 10 * time.Second

type Manager struct {
	store     core.Store
	sessions  Store
	opts      Options
	refreshes singleflight.Group
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

// Start starts a session for p. With Options.Refresh, where ts-auth alone
// signs in and Start only follows a scope change, it rotates the request's
// live session instead.
func (m *Manager) Start(w http.ResponseWriter, r *http.Request, p core.Principal) error {
	if m.opts.Refresh != nil {
		return m.rotate(w, r, p)
	}
	return m.start(w, r, p, "", false, Record{})
}

func (m *Manager) StartIfPasswordHash(w http.ResponseWriter, r *http.Request, p core.Principal, expectedHash string) error {
	return m.start(w, r, p, expectedHash, true, Record{})
}

// StartRefreshable starts a session that Options.Refresh rechecks with
// refresh from refreshAt on.
func (m *Manager) StartRefreshable(w http.ResponseWriter, r *http.Request, p core.Principal, refresh string, refreshAt time.Time) error {
	return m.start(w, r, p, "", false, Record{Refresh: refresh, RefreshAt: refreshAt})
}

func (m *Manager) start(w http.ResponseWriter, r *http.Request, p core.Principal, expectedHash string, checkHash bool, rec Record) error {
	ctx := r.Context()
	token, _, err := m.put(ctx, p, expectedHash, checkHash, rec)
	if err != nil {
		return err
	}
	// The new session is saved before the old one is deleted: if saving
	// fails the caller stays signed in, and an old record a store failed to
	// delete still expires.
	if old, hasOld := m.Token(r); hasOld {
		if err := m.sessions.Delete(ctx, sha256.Sum256([]byte(old))); err != nil {
			slog.Warn("delete replaced session", "error", err)
		}
	}
	m.setCookie(w, token)
	return nil
}

// rotate replaces the request's live session with one for p that carries its
// refresh state. The old session ends only if it is unchanged since it was
// read: one signed out meanwhile is not carried on, and one refreshed
// meanwhile is carried on with its new state.
func (m *Manager) rotate(w http.ResponseWriter, r *http.Request, p core.Principal) error {
	ctx := r.Context()
	old, ok := m.Token(r)
	if !ok {
		return core.ErrUnauthorized
	}
	oldKey := sha256.Sum256([]byte(old))
	for range 3 {
		prev, err := m.sessions.Get(ctx, oldKey, time.Now())
		if errors.Is(err, ErrNotFound) || err == nil && (prev.AccountID != p.AccountID || prev.UserID != p.UserID) {
			return core.ErrUnauthorized
		}
		if err != nil {
			return err
		}
		token, key, err := m.put(ctx, p, "", false, Record{Refresh: prev.Refresh, RefreshAt: prev.RefreshAt})
		if err != nil {
			return err
		}
		err = m.sessions.Replace(ctx, oldKey, prev, ended(prev))
		if err == nil {
			m.setCookie(w, token)
			return nil
		}
		if err := m.sessions.Delete(ctx, key); err != nil {
			slog.Warn("delete unused session", "error", err)
		}
		if !errors.Is(err, ErrNotFound) {
			return err
		}
	}
	return core.ErrUnauthorized
}

// put saves a new session for p with rec's refresh state and returns its
// token and key.
func (m *Manager) put(ctx context.Context, p core.Principal, expectedHash string, checkHash bool, rec Record) (string, [32]byte, error) {
	var authVersion uint64
	if err := m.store.ViewAs(ctx, p, func(a core.Account) error {
		if checkHash && (expectedHash == "" || a.User.PasswordHash != expectedHash) {
			return core.ErrConflict
		}
		authVersion = a.AuthVersion
		return nil
	}); err != nil {
		return "", [32]byte{}, err
	}
	var random [32]byte
	rand.Read(random[:])
	token := base64.RawURLEncoding.EncodeToString(random[:])
	key := sha256.Sum256([]byte(token))
	rec.AccountID, rec.UserID, rec.AuthVersion, rec.ExpiresAt = p.AccountID, p.UserID, authVersion, time.Now().Add(m.opts.Lifetime)
	return token, key, m.sessions.Put(ctx, key, rec)
}

func (m *Manager) setCookie(w http.ResponseWriter, token string) {
	http.SetCookie(w, &http.Cookie{
		Name: m.opts.CookieName, Value: token, Path: "/", HttpOnly: true,
		Secure: m.opts.Secure, SameSite: http.SameSiteLaxMode,
		MaxAge: int(m.opts.Lifetime.Seconds()),
	})
}

// ended is rec expired, which Store.Replace stores to end a session only if
// it is unchanged.
func ended(rec Record) Record {
	rec.ExpiresAt = time.Time{}
	return rec
}

func (m *Manager) Principal(r *http.Request) (core.Principal, bool) {
	token, ok := m.Token(r)
	if !ok {
		return core.Principal{}, false
	}
	return m.principalFor(r.Context(), token)
}

// NoticeScope identifies this browser login without exposing its bearer token.
// Domain separation keeps it distinct from the digest used to look up sessions.
func (m *Manager) NoticeScope(r *http.Request) string {
	token, ok := m.Token(r)
	if !ok {
		return ""
	}
	if _, valid := m.principalFor(r.Context(), token); !valid {
		return ""
	}
	digest := sha256.Sum256([]byte("status-notices\x00" + token))
	return hex.EncodeToString(digest[:16])
}

func (m *Manager) BearerPrincipal(r *http.Request) (core.Principal, bool) {
	scheme, token, ok := strings.Cut(r.Header.Get("Authorization"), " ")
	if !ok || !strings.EqualFold(scheme, "Bearer") || token == "" || strings.ContainsAny(token, " \t\r\n") {
		return core.Principal{}, false
	}
	return m.principalFor(r.Context(), token)
}

// UserToken is the ts-auth access token of the request's session, or empty
// for a session without one. It reads the session the request's Principal
// already resolved and refreshed, rechecking it first when its refresh state
// has no token yet rather than waiting for its RefreshAt.
func (m *Manager) UserToken(r *http.Request) string {
	token, ok := m.Token(r)
	if !ok || m.opts.UserToken == nil {
		return ""
	}
	key := sha256.Sum256([]byte(token))
	item, err := m.sessions.Get(r.Context(), key, time.Now())
	if err != nil || item.Refresh == "" {
		return ""
	}
	// A state without a token renews now, unless a recheck just failed
	// and set RefreshAt within refreshRetry: then it waits like any other.
	renewDue := func(rec Record) bool {
		_, renew := m.opts.UserToken(rec.Refresh)
		return renew && (refreshDue(rec) || rec.RefreshAt.After(time.Now().Add(refreshRetry)))
	}
	user, _ := m.opts.UserToken(item.Refresh)
	if !renewDue(item) || m.opts.Refresh == nil {
		return user
	}
	item, err = m.refresh(r.Context(), key, renewDue)
	if err != nil {
		return ""
	}
	user, _ = m.opts.UserToken(item.Refresh)
	return user
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
	if err == nil && item.Refresh != "" && m.opts.Refresh != nil && !time.Now().Before(item.RefreshAt) {
		item, err = m.refresh(ctx, key, refreshDue)
	}
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

// refreshDue reports whether a session's RefreshAt has passed.
func refreshDue(rec Record) bool { return !time.Now().Before(rec.RefreshAt) }

// refreshRetry is how long a session whose recheck failed waits before the
// next attempt. A failure sets RefreshAt that close, so an early renewal
// that failed waits too instead of retrying on every request.
const refreshRetry = time.Minute

// refresh rechecks the session under key with Options.Refresh if it is still
// due, once however many of its requests arrive together, and returns its
// record afterwards or ErrNotFound once the session ended.
func (m *Manager) refresh(ctx context.Context, key [32]byte, due func(Record) bool) (Record, error) {
	result, err, _ := m.refreshes.Do(string(key[:]), func() (any, error) {
		// The outcome is saved even if the request that asked for it ends,
		// since the provider may already have replaced the refresh token.
		ctx, cancel := context.WithTimeout(context.WithoutCancel(ctx), refreshTimeout)
		defer cancel()
		current, err := m.sessions.Get(ctx, key, time.Now())
		if err != nil || !due(current) {
			return current, err
		}
		next, at, err := m.opts.Refresh(ctx, current.Refresh)
		updated := current
		switch {
		case errors.Is(err, core.ErrUnauthorized):
			updated = ended(current)
		case err != nil:
			slog.Warn("refresh session", "error", err)
			updated.RefreshAt = time.Now().Add(refreshRetry)
		default:
			updated.Refresh, updated.RefreshAt = next, at
		}
		// Only an unchanged session is updated or ended: one signed out or
		// rotated during the refresh stays so, and one another machine
		// refreshed meanwhile, spending the token this attempt used, is read
		// back.
		if err := m.sessions.Replace(ctx, key, current, updated); errors.Is(err, ErrNotFound) {
			latest, err := m.sessions.Get(ctx, key, time.Now())
			if errors.Is(err, ErrNotFound) && updated.Refresh != current.Refresh {
				// Signed out meanwhile: end the sign-in this refresh renewed.
				m.revoke(ctx, updated.Refresh)
			}
			return latest, err
		} else if err != nil {
			return Record{}, err
		}
		// The session may also have expired while ts-auth answered.
		if !time.Now().Before(updated.ExpiresAt) {
			return Record{}, ErrNotFound
		}
		return updated, nil
	})
	if err != nil {
		return Record{}, err
	}
	return result.(Record), nil
}

func (m *Manager) End(w http.ResponseWriter, r *http.Request) error {
	token, _ := m.Token(r)
	var err error
	if token != "" {
		err = m.end(r.Context(), sha256.Sum256([]byte(token)))
	}
	if scheme, bearer, ok := strings.Cut(r.Header.Get("Authorization"), " "); ok && strings.EqualFold(scheme, "Bearer") && bearer != "" && !strings.ContainsAny(bearer, " \t\r\n") && bearer != token {
		// Try both tokens even if one deletion fails.
		err = errors.Join(err, m.end(r.Context(), sha256.Sum256([]byte(bearer))))
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

// end deletes the session under key, then revokes its refresh state. The
// session is looked up only for Options.Revoke, and a failed lookup only
// skips revocation, so an account outage never prevents logout. It ends the
// record only as last read, so the state revoked is the newest a concurrent
// refresh saved; refresh revokes what it renews for a session ended
// meanwhile.
func (m *Manager) end(ctx context.Context, key [32]byte) error {
	var refresh string
	// ponytail: three reads; more concurrent refreshes of one session than
	// that would need a lock across the shared store.
	for attempt := 0; m.opts.Revoke != nil && attempt < 3; attempt++ {
		record, err := m.sessions.Get(ctx, key, time.Now())
		if err != nil {
			break
		}
		if err := m.sessions.Replace(ctx, key, record, ended(record)); err == nil {
			refresh = record.Refresh
			break
		} else if !errors.Is(err, ErrNotFound) {
			break
		}
	}
	if err := m.sessions.Delete(ctx, key); err != nil {
		return err
	}
	m.revoke(ctx, refresh)
	return nil
}

// revoke hands an ended session's refresh state to Options.Revoke, best
// effort and after the session is gone, so a slow or cancelled call never
// keeps anyone signed in.
func (m *Manager) revoke(ctx context.Context, refresh string) {
	if m.opts.Revoke == nil || refresh == "" {
		return
	}
	ctx, cancel := context.WithTimeout(context.WithoutCancel(ctx), refreshTimeout)
	defer cancel()
	if err := m.opts.Revoke(ctx, refresh); err != nil {
		slog.Warn("revoke session", "error", err)
	}
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
