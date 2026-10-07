package session

import (
	"context"
	"crypto/sha256"
	"errors"
	"fmt"
	"net/http"
	"net/http/httptest"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/core/memstore"
)

// refreshHook is an Options.Refresh that counts its calls.
type refreshHook struct {
	calls atomic.Int32
	fn    func(ctx context.Context, refresh string) (string, time.Time, error)
}

func (h *refreshHook) refresh(ctx context.Context, refresh string) (string, time.Time, error) {
	h.calls.Add(1)
	return h.fn(ctx, refresh)
}

// refreshFixture starts a session refreshable with "r1" from refreshAt and
// returns a request carrying its cookie and the session's store key.
func refreshFixture(t *testing.T, sessions Store, refreshAt time.Time, hook *refreshHook) (*Manager, core.Store, *http.Request, [32]byte) {
	t.Helper()
	accounts := memstore.New()
	a, err := accounts.CreateAccount(t.Context(), core.User{Name: "Refresh", Email: "refresh@example.test", PasswordHash: "unused"})
	if err != nil {
		t.Fatal(err)
	}
	m := New(accounts, Options{Store: sessions, Refresh: hook.refresh})
	r, key := startRefreshable(t, m, a.Principal(), refreshAt)
	return m, accounts, r, key
}

// startRefreshable starts a session for p refreshable with "r1" from
// refreshAt and returns a request carrying its cookie and its store key.
func startRefreshable(t *testing.T, m *Manager, p core.Principal, refreshAt time.Time) (*http.Request, [32]byte) {
	t.Helper()
	r := sessionRequest(t, m, p, httptest.NewRequest(http.MethodGet, "/", nil), func(w http.ResponseWriter, r *http.Request, p core.Principal) error {
		return m.StartRefreshable(w, r, p, "r1", refreshAt)
	})
	return r, cookieKey(t, m, r)
}

func cookieKey(t *testing.T, m *Manager, r *http.Request) [32]byte {
	t.Helper()
	token, ok := m.Token(r)
	if !ok {
		t.Fatal("request carries no session cookie")
	}
	return sha256.Sum256([]byte(token))
}

// sessionRequest runs start for p on r and returns a request carrying the
// session cookie it set.
func sessionRequest(t *testing.T, m *Manager, p core.Principal, r *http.Request, start func(http.ResponseWriter, *http.Request, core.Principal) error) *http.Request {
	t.Helper()
	w := httptest.NewRecorder()
	if err := start(w, r, p); err != nil {
		t.Fatal(err)
	}
	next := httptest.NewRequest(http.MethodGet, "/", nil)
	for _, c := range w.Result().Cookies() {
		next.AddCookie(c)
	}
	return next
}

func storedRecord(t *testing.T, sessions Store, key [32]byte) Record {
	t.Helper()
	rec, err := sessions.Get(t.Context(), key, time.Now())
	if err != nil {
		t.Fatal(err)
	}
	return rec
}

func TestRefreshNotDue(t *testing.T) {
	hook := &refreshHook{fn: func(context.Context, string) (string, time.Time, error) {
		return "", time.Time{}, errors.New("unexpected refresh")
	}}
	m, _, r, _ := refreshFixture(t, NewMemoryStore(), time.Now().Add(time.Hour), hook)
	if _, ok := m.Principal(r); !ok {
		t.Fatal("session not yet due is invalid")
	}
	if n := hook.calls.Load(); n != 0 {
		t.Fatalf("refresh calls = %d, want 0", n)
	}
}

func TestRefreshDueKeepsReturnedState(t *testing.T) {
	next := time.Now().Add(time.Hour)
	var got string
	hook := &refreshHook{fn: func(_ context.Context, refresh string) (string, time.Time, error) {
		got = refresh
		return "r2", next, nil
	}}
	sessions := NewMemoryStore()
	m, _, r, key := refreshFixture(t, sessions, time.Now(), hook)
	if _, ok := m.Principal(r); !ok {
		t.Fatal("refreshed session is invalid")
	}
	if n := hook.calls.Load(); n != 1 || got != "r1" {
		t.Fatalf("refresh calls = %d with %q, want 1 with r1", n, got)
	}
	if rec := storedRecord(t, sessions, key); rec.Refresh != "r2" || !rec.RefreshAt.Equal(next) {
		t.Fatalf("record refresh = %q at %v, want r2 at %v", rec.Refresh, rec.RefreshAt, next)
	}
}

func TestRefreshUnauthorizedEndsSession(t *testing.T) {
	hook := &refreshHook{fn: func(context.Context, string) (string, time.Time, error) {
		return "", time.Time{}, fmt.Errorf("token revoked: %w", core.ErrUnauthorized)
	}}
	sessions := NewMemoryStore()
	m, _, r, key := refreshFixture(t, sessions, time.Now(), hook)
	for i := range 2 {
		if _, ok := m.Principal(r); ok {
			t.Fatalf("request %d: revoked session is valid", i)
		}
	}
	if n := hook.calls.Load(); n != 1 {
		t.Fatalf("refresh calls = %d, want 1", n)
	}
	if _, err := sessions.Get(t.Context(), key, time.Now()); !errors.Is(err, ErrNotFound) {
		t.Fatalf("revoked session lookup = %v, want ErrNotFound", err)
	}
}

func TestRefreshTransientErrorRetriesLater(t *testing.T) {
	hook := &refreshHook{fn: func(context.Context, string) (string, time.Time, error) {
		return "", time.Time{}, errors.New("ts-auth unreachable")
	}}
	sessions := NewMemoryStore()
	m, _, r, key := refreshFixture(t, sessions, time.Now(), hook)
	for i := range 2 {
		if _, ok := m.Principal(r); !ok {
			t.Fatalf("request %d: session is invalid after a transient refresh error", i)
		}
	}
	if n := hook.calls.Load(); n != 1 {
		t.Fatalf("refresh calls = %d, want 1", n)
	}
	rec := storedRecord(t, sessions, key)
	if until := time.Until(rec.RefreshAt); rec.Refresh != "r1" || until < 50*time.Second || until > time.Minute {
		t.Fatalf("record refresh = %q in %v, want r1 in about a minute", rec.Refresh, until)
	}
}

func TestRefreshConcurrentRequestsCallOnce(t *testing.T) {
	const n = 8
	var started sync.WaitGroup
	started.Add(n)
	hook := &refreshHook{fn: func(context.Context, string) (string, time.Time, error) {
		started.Wait()
		return "r2", time.Now().Add(time.Hour), nil
	}}
	m, _, r, _ := refreshFixture(t, NewMemoryStore(), time.Now(), hook)
	var valid atomic.Int32
	var done sync.WaitGroup
	for range n {
		done.Go(func() {
			started.Done()
			if _, ok := m.Principal(r.Clone(r.Context())); ok {
				valid.Add(1)
			}
		})
	}
	done.Wait()
	if calls := hook.calls.Load(); calls != 1 || valid.Load() != n {
		t.Fatalf("refresh calls = %d, valid = %d; want 1 and %d", calls, valid.Load(), n)
	}
}

func TestRefreshUnauthorizedKeepsSessionAnotherMachineRefreshed(t *testing.T) {
	sessions := NewMemoryStore()
	var key [32]byte
	hook := &refreshHook{fn: func(ctx context.Context, _ string) (string, time.Time, error) {
		rec, err := sessions.Get(ctx, key, time.Now())
		if err != nil {
			return "", time.Time{}, err
		}
		rec.Refresh, rec.RefreshAt = "other", time.Now().Add(time.Hour)
		if err := sessions.Put(ctx, key, rec); err != nil {
			return "", time.Time{}, err
		}
		return "", time.Time{}, core.ErrUnauthorized
	}}
	m, _, r, k := refreshFixture(t, sessions, time.Now(), hook)
	key = k
	if _, ok := m.Principal(r); !ok {
		t.Fatal("session refreshed by another machine is invalid")
	}
	if rec := storedRecord(t, sessions, key); rec.Refresh != "other" {
		t.Fatalf("record refresh = %q, want other", rec.Refresh)
	}
}

// TestRefreshKeepsSignOutDuringRefresh signs out while the refresh is in
// flight: its successful result must not bring the session back.
func TestRefreshKeepsSignOutDuringRefresh(t *testing.T) {
	var m *Manager
	var r *http.Request
	hook := &refreshHook{fn: func(context.Context, string) (string, time.Time, error) {
		if err := m.End(httptest.NewRecorder(), r); err != nil {
			return "", time.Time{}, err
		}
		return "r2", time.Now().Add(time.Hour), nil
	}}
	sessions := NewMemoryStore()
	m, _, r, key := refreshFixture(t, sessions, time.Now(), hook)
	if _, ok := m.Principal(r); ok {
		t.Fatal("session signed out during its refresh is valid")
	}
	if _, err := sessions.Get(t.Context(), key, time.Now()); !errors.Is(err, ErrNotFound) {
		t.Fatalf("signed-out session lookup = %v, want ErrNotFound", err)
	}
}

// TestRefreshKeepsAnotherMachinesRefresh has another machine store a newer
// refresh while this one refreshes; the later write must not overwrite it.
func TestRefreshKeepsAnotherMachinesRefresh(t *testing.T) {
	sessions := NewMemoryStore()
	var key [32]byte
	hook := &refreshHook{fn: func(ctx context.Context, _ string) (string, time.Time, error) {
		rec, err := sessions.Get(ctx, key, time.Now())
		if err != nil {
			return "", time.Time{}, err
		}
		rec.Refresh, rec.RefreshAt = "other", time.Now().Add(time.Hour)
		if err := sessions.Put(ctx, key, rec); err != nil {
			return "", time.Time{}, err
		}
		return "r2", time.Now().Add(time.Hour), nil
	}}
	m, _, r, k := refreshFixture(t, sessions, time.Now(), hook)
	key = k
	if _, ok := m.Principal(r); !ok {
		t.Fatal("session refreshed on two machines is invalid")
	}
	if rec := storedRecord(t, sessions, key); rec.Refresh != "other" {
		t.Fatalf("record refresh = %q, want the other machine's", rec.Refresh)
	}
}

func TestRefreshDoesNotOutliveSessionExpiry(t *testing.T) {
	accounts := memstore.New()
	a, err := accounts.CreateAccount(t.Context(), core.User{Name: "Expiry", Email: "expiry@example.test", PasswordHash: "unused"})
	if err != nil {
		t.Fatal(err)
	}
	const lifetime = 100 * time.Millisecond
	hook := &refreshHook{fn: func(context.Context, string) (string, time.Time, error) {
		time.Sleep(2 * lifetime)
		return "r2", time.Now().Add(time.Hour), nil
	}}
	m := New(accounts, Options{Store: NewMemoryStore(), Refresh: hook.refresh, Lifetime: lifetime})
	r, _ := startRefreshable(t, m, a.Principal(), time.Now())
	if _, ok := m.Principal(r); ok {
		t.Fatal("session that expired during its refresh is valid")
	}
}

func TestRefreshOutlivesCanceledRequest(t *testing.T) {
	var hookErr error
	hook := &refreshHook{fn: func(ctx context.Context, _ string) (string, time.Time, error) {
		hookErr = ctx.Err()
		return "r2", time.Now().Add(time.Hour), nil
	}}
	sessions := NewMemoryStore()
	m, _, r, key := refreshFixture(t, sessions, time.Now(), hook)
	ctx, cancel := context.WithCancel(r.Context())
	cancel()
	m.Principal(r.WithContext(ctx))
	if hookErr != nil {
		t.Fatalf("refresh context error = %v, want none", hookErr)
	}
	if rec := storedRecord(t, sessions, key); rec.Refresh != "r2" {
		t.Fatalf("record refresh = %q, want r2", rec.Refresh)
	}
}

// refreshedOnPut is a Store whose first Put, the rotated session's, is
// followed by another request refreshing the old session.
type refreshedOnPut struct {
	Store
	old  [32]byte
	once sync.Once
}

func (s *refreshedOnPut) Put(ctx context.Context, key [32]byte, rec Record) error {
	if err := s.Store.Put(ctx, key, rec); err != nil {
		return err
	}
	var err error
	s.once.Do(func() {
		var prev Record
		if prev, err = s.Store.Get(ctx, s.old, time.Now()); err == nil {
			prev.Refresh = "r2"
			err = s.Store.Put(ctx, s.old, prev)
		}
	})
	return err
}

func TestStartRotatesRefreshableSession(t *testing.T) {
	refreshAt := time.Now().Add(time.Hour)
	hook := &refreshHook{fn: func(context.Context, string) (string, time.Time, error) {
		return "", time.Time{}, errors.New("unexpected refresh")
	}}
	sessions := NewMemoryStore()
	m, accounts, r, oldKey := refreshFixture(t, sessions, refreshAt, hook)
	p, ok := m.Principal(r)
	if !ok {
		t.Fatal("refreshable session is invalid")
	}
	other, err := accounts.CreateAccount(t.Context(), core.User{Name: "Other", Email: "other@example.test", PasswordHash: "unused"})
	if err != nil {
		t.Fatal(err)
	}
	for name, start := range map[string]func() error{
		"another user": func() error { return m.Start(httptest.NewRecorder(), r, other.Principal()) },
		"no session":   func() error { return m.Start(httptest.NewRecorder(), httptest.NewRequest(http.MethodGet, "/", nil), p) },
	} {
		if err := start(); !errors.Is(err, core.ErrUnauthorized) {
			t.Fatalf("Start for %s = %v, want ErrUnauthorized", name, err)
		}
	}

	rotated := sessionRequest(t, m, p, r, m.Start)
	if rec := storedRecord(t, sessions, cookieKey(t, m, rotated)); rec.Refresh != "r1" || !rec.RefreshAt.Equal(refreshAt) {
		t.Fatalf("rotated refresh = %q at %v, want r1 at %v", rec.Refresh, rec.RefreshAt, refreshAt)
	}
	if _, err := sessions.Get(t.Context(), oldKey, time.Now()); !errors.Is(err, ErrNotFound) {
		t.Fatalf("replaced session lookup = %v, want ErrNotFound", err)
	}
	// The replaced session, signed out by the rotation, cannot rotate again.
	if err := m.Start(httptest.NewRecorder(), r, p); !errors.Is(err, core.ErrUnauthorized) {
		t.Fatalf("Start from the replaced session = %v, want ErrUnauthorized", err)
	}
}

func TestStartRotationCarriesRefreshMadeMeanwhile(t *testing.T) {
	hook := &refreshHook{fn: func(context.Context, string) (string, time.Time, error) {
		return "", time.Time{}, errors.New("unexpected refresh")
	}}
	sessions := &refreshedOnPut{Store: NewMemoryStore()}
	sessions.once.Do(func() {}) // not while the fixture starts its session
	m, _, r, oldKey := refreshFixture(t, sessions, time.Now().Add(time.Hour), hook)
	p, ok := m.Principal(r)
	if !ok {
		t.Fatal("refreshable session is invalid")
	}
	sessions.old, sessions.once = oldKey, sync.Once{}
	rotated := sessionRequest(t, m, p, r, m.Start)
	if rec := storedRecord(t, sessions, cookieKey(t, m, rotated)); rec.Refresh != "r2" {
		t.Fatalf("rotated refresh = %q, want the r2 refreshed meanwhile", rec.Refresh)
	}
	if entries, err := sessions.ListUser(t.Context(), p.AccountID, p.UserID, time.Now()); err != nil || len(entries) != 1 {
		t.Fatalf("live sessions = %d, %v; want only the rotated one", len(entries), err)
	}
}

func TestStartReturnsPreviousSessionLookupError(t *testing.T) {
	hook := &refreshHook{fn: func(context.Context, string) (string, time.Time, error) {
		return "", time.Time{}, errors.New("unexpected refresh")
	}}
	sessions := &sessionLookupStore{Store: NewMemoryStore()}
	m, _, r, _ := refreshFixture(t, sessions, time.Now().Add(time.Hour), hook)
	p, ok := m.Principal(r)
	if !ok {
		t.Fatal("refreshable session is invalid")
	}
	sessions.getErr = errors.New("transport failed")
	if err := m.Start(httptest.NewRecorder(), r, p); !errors.Is(err, sessions.getErr) {
		t.Fatalf("Start = %v, want %v", err, sessions.getErr)
	}
}

// revoked records the refresh states Options.Revoke is given.
type revoked struct {
	mu     sync.Mutex
	states []string
}

func (v *revoked) revoke(_ context.Context, refresh string) error {
	v.mu.Lock()
	defer v.mu.Unlock()
	v.states = append(v.states, refresh)
	return nil
}

func (v *revoked) got() []string {
	v.mu.Lock()
	defer v.mu.Unlock()
	return append([]string(nil), v.states...)
}

// TestSignOutDuringRefreshRevokesTheRenewal signs out while ts-auth renews
// the session: the token the refresh could not save is revoked too.
func TestSignOutDuringRefreshRevokesTheRenewal(t *testing.T) {
	var m *Manager
	var r *http.Request
	hook := &refreshHook{fn: func(context.Context, string) (string, time.Time, error) {
		if err := m.End(httptest.NewRecorder(), r); err != nil {
			return "", time.Time{}, err
		}
		return "r2", time.Now().Add(time.Hour), nil
	}}
	accounts := memstore.New()
	a, err := accounts.CreateAccount(t.Context(), core.User{Name: "Refresh", Email: "refresh@example.test", PasswordHash: "unused"})
	if err != nil {
		t.Fatal(err)
	}
	var v revoked
	m = New(accounts, Options{Store: NewMemoryStore(), Refresh: hook.refresh, Revoke: v.revoke})
	r, _ = startRefreshable(t, m, a.Principal(), time.Now())
	if _, ok := m.Principal(r); ok {
		t.Fatal("session signed out during its refresh is valid")
	}
	if got := v.got(); fmt.Sprint(got) != "[r1 r2]" {
		t.Fatalf("revoked %v, want the state signed out and the one renewed meanwhile", got)
	}
}

// rotatedFirst is a Store in which another refresh saves "r2" just before
// the first Replace, as one racing a sign-out would.
type rotatedFirst struct {
	Store
	once sync.Once
}

func (s *rotatedFirst) Replace(ctx context.Context, key [32]byte, old, next Record) error {
	s.once.Do(func() {
		renewed := old
		renewed.Refresh = "r2"
		_ = s.Store.Replace(ctx, key, old, renewed)
	})
	return s.Store.Replace(ctx, key, old, next)
}

func TestSignOutRevokesTheNewestRefresh(t *testing.T) {
	accounts := memstore.New()
	a, err := accounts.CreateAccount(t.Context(), core.User{Name: "Refresh", Email: "refresh@example.test", PasswordHash: "unused"})
	if err != nil {
		t.Fatal(err)
	}
	var v revoked
	sessions := &rotatedFirst{Store: NewMemoryStore()}
	m := New(accounts, Options{Store: sessions, Refresh: (&refreshHook{}).refresh, Revoke: v.revoke})
	r, key := startRefreshable(t, m, a.Principal(), time.Now().Add(time.Hour))
	if err := m.End(httptest.NewRecorder(), r); err != nil {
		t.Fatal(err)
	}
	if got := v.got(); fmt.Sprint(got) != "[r2]" {
		t.Fatalf("revoked %v, want the refresh saved during sign-out", got)
	}
	if _, err := sessions.Get(t.Context(), key, time.Now()); !errors.Is(err, ErrNotFound) {
		t.Fatalf("signed-out session lookup = %v, want ErrNotFound", err)
	}
}

// TestUserTokenRenewsTokenlessSessionsOnceUntilRetry covers sessions saved
// before they kept the access token: the first lookup rechecks at once, and
// a failed recheck waits refreshRetry instead of repeating on every lookup.
func TestUserTokenRenewsTokenlessSessionsOnceUntilRetry(t *testing.T) {
	accounts := memstore.New()
	a, err := accounts.CreateAccount(t.Context(), core.User{Name: "Tokenless", Email: "tokenless@example.test", PasswordHash: "unused"})
	if err != nil {
		t.Fatal(err)
	}
	hook := &refreshHook{fn: func(context.Context, string) (string, time.Time, error) {
		return "", time.Time{}, errors.New("ts-auth unavailable")
	}}
	userToken := func(refresh string) (string, bool) {
		if refresh == "r1" {
			return "", true
		}
		return "token-" + refresh, false
	}
	m := New(accounts, Options{Refresh: hook.refresh, UserToken: userToken})
	r, _ := startRefreshable(t, m, a.Principal(), time.Now().Add(time.Hour))
	for range 3 {
		if token := m.UserToken(r); token != "" {
			t.Fatalf("token %q from a failed recheck", token)
		}
	}
	if calls := hook.calls.Load(); calls != 1 {
		t.Fatalf("rechecks = %d, want one until refreshRetry passes", calls)
	}
	hook.fn = func(context.Context, string) (string, time.Time, error) { return "r2", time.Now().Add(time.Hour), nil }
	// A normal recheck due within refreshRetry is no failure backoff: the
	// session still renews at once.
	for _, refreshAt := range []time.Duration{time.Hour, 30 * time.Second} {
		m2 := New(accounts, Options{Refresh: hook.refresh, UserToken: userToken})
		r2, key := startRefreshable(t, m2, a.Principal(), time.Now().Add(refreshAt))
		if token := m2.UserToken(r2); token != "token-r2" {
			t.Fatalf("token after an early renewal of a session due in %v = %q, want token-r2", refreshAt, token)
		}
		if rec := storedRecord(t, m2.sessions, key); !rec.RetryAt.IsZero() {
			t.Fatalf("RetryAt after a successful recheck = %v, want cleared", rec.RetryAt)
		}
	}
}
