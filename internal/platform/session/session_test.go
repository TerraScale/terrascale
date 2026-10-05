package session

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"fmt"
	"net/http"
	"net/http/httptest"
	"slices"
	"strings"
	"testing"
	"time"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/core/memstore"
)

type accountLookupStore struct {
	core.Store
	err   error
	calls int
}

func (s *accountLookupStore) SessionPrincipal(ctx context.Context, accountID, userID string, version uint64) (core.Principal, error) {
	s.calls++
	if s.err != nil {
		return core.Principal{}, s.err
	}
	return s.Store.SessionPrincipal(ctx, accountID, userID, version)
}

type sessionLookupStore struct {
	Store
	getErr     error
	deleteErrs map[[32]byte]error
	gets       int
	deleted    [][32]byte
}

func (s *sessionLookupStore) Get(ctx context.Context, key [32]byte, now time.Time) (Record, error) {
	s.gets++
	if s.getErr != nil {
		return Record{}, s.getErr
	}
	return s.Store.Get(ctx, key, now)
}

func (s *sessionLookupStore) Delete(ctx context.Context, key [32]byte) error {
	s.deleted = append(s.deleted, key)
	if err := s.deleteErrs[key]; err != nil {
		return err
	}
	return s.Store.Delete(ctx, key)
}

func sessionFixture(t *testing.T) (*Manager, *accountLookupStore, *sessionLookupStore, core.Principal, [32]byte) {
	t.Helper()
	accounts := &accountLookupStore{Store: memstore.New()}
	a, err := accounts.CreateAccount(t.Context(), core.User{Name: "Session", Email: "session@example.test", PasswordHash: "fixture-hash"})
	if err != nil {
		t.Fatal(err)
	}
	sessions := &sessionLookupStore{Store: NewMemoryStore()}
	key := sha256.Sum256([]byte("cookie-token"))
	if err := sessions.Put(t.Context(), key, Record{AccountID: a.ID, UserID: a.User.ID, AuthVersion: a.AuthVersion, ExpiresAt: time.Now().Add(time.Hour)}); err != nil {
		t.Fatal(err)
	}
	return New(accounts, Options{Store: sessions}), accounts, sessions, a.Principal(), key
}

func TestCLIGrantPrincipalPreservesLookupErrors(t *testing.T) {
	for _, stage := range []string{"session", "account"} {
		for _, lookupErr := range []error{errors.New("transport failed"), core.ErrUnavailable, context.Canceled, context.DeadlineExceeded} {
			t.Run(stage+"/"+lookupErr.Error(), func(t *testing.T) {
				m, accounts, sessions, _, key := sessionFixture(t)
				wrapped := fmt.Errorf("lookup: %w", lookupErr)
				if stage == "session" {
					sessions.getErr = wrapped
				} else {
					accounts.err = wrapped
				}
				p, valid, err := m.CLIGrantPrincipal(t.Context(), hex.EncodeToString(key[:]))
				if p != (core.Principal{}) || valid || !errors.Is(err, lookupErr) {
					t.Fatalf("principal=%+v valid=%v error=%v; want original failure", p, valid, err)
				}
				if stage == "session" && accounts.calls != 0 {
					t.Fatal("account looked up after session lookup failed")
				}
				r := httptest.NewRequest(http.MethodGet, "/", nil)
				r.AddCookie(&http.Cookie{Name: m.opts.CookieName, Value: "cookie-token"})
				r.Header.Set("Authorization", "Bearer cookie-token")
				if _, valid := m.Principal(r); valid {
					t.Fatal("cookie authenticated during lookup failure")
				}
				if _, valid := m.BearerPrincipal(r); valid {
					t.Fatal("bearer authenticated during lookup failure")
				}
			})
		}
	}
}

func TestCLIGrantPrincipalInvalidSessions(t *testing.T) {
	for _, state := range []string{"malformed", "absent", "expired", "revoked", "credentials changed"} {
		t.Run(state, func(t *testing.T) {
			m, accounts, sessions, expected, key := sessionFixture(t)
			binding := hex.EncodeToString(key[:])
			switch state {
			case "malformed":
				for _, malformed := range []string{"", "short", strings.Repeat("z", 64)} {
					if p, valid, err := m.CLIGrantPrincipal(t.Context(), malformed); p != (core.Principal{}) || valid || err != nil {
						t.Fatalf("malformed binding %q: principal=%+v valid=%v error=%v", malformed, p, valid, err)
					}
				}
				if sessions.gets != 0 || accounts.calls != 0 {
					t.Fatal("malformed binding reached a store")
				}
				return
			case "absent":
				key = sha256.Sum256([]byte("missing-token"))
				binding = hex.EncodeToString(key[:])
			case "expired":
				if err := sessions.Put(t.Context(), key, Record{AccountID: expected.AccountID, UserID: expected.UserID, ExpiresAt: time.Now().Add(-time.Second)}); err != nil {
					t.Fatal(err)
				}
			case "revoked":
				if err := m.RevokeUser(t.Context(), expected.AccountID, expected.UserID); err != nil {
					t.Fatal(err)
				}
			case "credentials changed":
				if err := accounts.Update(t.Context(), expected.AccountID, func(a *core.Account) error { a.User.PasswordHash = "new-fixture-hash"; return nil }); err != nil {
					t.Fatal(err)
				}
			}
			if p, valid, err := m.CLIGrantPrincipal(t.Context(), binding); p != (core.Principal{}) || valid || err != nil {
				t.Fatalf("principal=%+v valid=%v error=%v; want invalid credentials", p, valid, err)
			}
			if state != "credentials changed" && accounts.calls != 0 {
				t.Fatal("invalid session reached account lookup")
			}
		})
	}
	for _, domainErr := range []error{core.ErrUnauthorized, core.ErrForbidden, core.ErrNotFound} {
		t.Run(domainErr.Error(), func(t *testing.T) {
			m, accounts, _, _, key := sessionFixture(t)
			accounts.err = fmt.Errorf("account: %w", domainErr)
			if p, valid, err := m.CLIGrantPrincipal(t.Context(), hex.EncodeToString(key[:])); p != (core.Principal{}) || valid || err != nil {
				t.Fatalf("principal=%+v valid=%v error=%v; want invalid credentials", p, valid, err)
			}
		})
	}
}

func TestCLIGrantPrincipalHealthySession(t *testing.T) {
	m, _, _, expected, _ := sessionFixture(t)
	r := httptest.NewRequest(http.MethodGet, "/", nil)
	r.AddCookie(&http.Cookie{Name: m.opts.CookieName, Value: "cookie-token"})
	binding, valid := m.BindCLIGrant(r)
	if !valid {
		t.Fatal("healthy session could not bind CLI grant")
	}
	if p, valid, err := m.CLIGrantPrincipal(t.Context(), binding); p != expected || !valid || err != nil {
		t.Fatalf("principal=%+v valid=%v error=%v; want %+v", p, valid, err, expected)
	}
}

func TestEndRetriesBothTokensWithoutLookup(t *testing.T) {
	for _, failed := range []string{"cookie", "bearer", "both"} {
		t.Run(failed, func(t *testing.T) {
			m, accounts, sessions, expected, cookieKey := sessionFixture(t)
			bearerKey := sha256.Sum256([]byte("bearer-token"))
			if err := sessions.Put(t.Context(), bearerKey, Record{AccountID: expected.AccountID, UserID: expected.UserID, ExpiresAt: time.Now().Add(time.Hour)}); err != nil {
				t.Fatal(err)
			}
			cookieErr, bearerErr := errors.New("cookie deletion failed"), fmt.Errorf("bearer deletion: %w", core.ErrUnavailable)
			sessions.deleteErrs = make(map[[32]byte]error)
			if failed != "bearer" {
				sessions.deleteErrs[cookieKey] = cookieErr
			}
			if failed != "cookie" {
				sessions.deleteErrs[bearerKey] = bearerErr
			}
			r := httptest.NewRequest(http.MethodPost, "/logout", nil)
			r.AddCookie(&http.Cookie{Name: m.opts.CookieName, Value: "cookie-token"})
			r.Header.Set("Authorization", "Bearer bearer-token")
			w := httptest.NewRecorder()
			err := m.End(w, r)
			if err == nil || (failed != "bearer" && !errors.Is(err, cookieErr)) || (failed != "cookie" && !errors.Is(err, core.ErrUnavailable)) {
				t.Fatalf("End error=%v; lost deletion failure", err)
			}
			if !slices.Equal(sessions.deleted, [][32]byte{cookieKey, bearerKey}) || sessions.gets != 0 || accounts.calls != 0 {
				t.Fatalf("deleted=%v session lookups=%d account lookups=%d", sessions.deleted, sessions.gets, accounts.calls)
			}
			if w.Header().Get("Set-Cookie") != "" {
				t.Fatal("failed logout cleared the cookie needed for retry")
			}
			sessions.deleteErrs = nil
			w = httptest.NewRecorder()
			if err := m.End(w, r); err != nil {
				t.Fatal(err)
			}
			if !slices.Equal(sessions.deleted, [][32]byte{cookieKey, bearerKey, cookieKey, bearerKey}) || sessions.gets != 0 || accounts.calls != 0 {
				t.Fatal("retry did not delete both tokens without a lookup")
			}
			cookies := w.Result().Cookies()
			if len(cookies) != 1 || cookies[0].Name != m.opts.CookieName || cookies[0].Value != "" || cookies[0].MaxAge != -1 {
				t.Fatalf("successful logout cookies=%v; want clearing cookie", cookies)
			}
			for _, key := range [][32]byte{cookieKey, bearerKey} {
				if _, err := sessions.Store.Get(t.Context(), key, time.Now()); !errors.Is(err, ErrNotFound) {
					t.Fatalf("token survived retry: %v", err)
				}
			}
		})
	}
}

func TestEndDeduplicatesAndValidatesBearer(t *testing.T) {
	for _, authorization := range []string{"", "Bearer cookie-token", "bEaReR cookie-token", "Basic bearer-token", "Bearer ", "Bearer bearer token", "Bearer bearer\ttoken", "Bearer bearer-token"} {
		t.Run(authorization, func(t *testing.T) {
			m, accounts, sessions, _, cookieKey := sessionFixture(t)
			r := httptest.NewRequest(http.MethodPost, "/logout", nil)
			r.AddCookie(&http.Cookie{Name: m.opts.CookieName, Value: "cookie-token"})
			r.Header.Set("Authorization", authorization)
			if err := m.End(httptest.NewRecorder(), r); err != nil {
				t.Fatal(err)
			}
			want := [][32]byte{cookieKey}
			if authorization == "Bearer bearer-token" {
				want = append(want, sha256.Sum256([]byte("bearer-token")))
			}
			if !slices.Equal(sessions.deleted, want) || sessions.gets != 0 || accounts.calls != 0 {
				t.Fatalf("deleted=%v; want %v without lookups", sessions.deleted, want)
			}
		})
	}
}

func TestNoticeScopeFollowsTheBrowserLogin(t *testing.T) {
	store := memstore.New()
	account, err := store.CreateAccount(t.Context(), core.User{Name: "Ada", Email: "ada@example.test", PasswordHash: "test-hash"})
	if err != nil {
		t.Fatal(err)
	}
	manager := New(store, Options{CookieName: "custom-session"})
	principal := core.Principal{AccountID: account.ID, UserID: account.User.ID}
	request := httptest.NewRequest(http.MethodGet, "/", nil)
	if manager.NoticeScope(request) != "" {
		t.Fatal("anonymous request received a notice scope")
	}
	start := func(request *http.Request) *http.Request {
		response := httptest.NewRecorder()
		if err := manager.Start(response, request, principal); err != nil {
			t.Fatal(err)
		}
		next := httptest.NewRequest(http.MethodGet, "/", nil)
		next.AddCookie(response.Result().Cookies()[0])
		return next
	}
	first := start(request)
	scope := manager.NoticeScope(first)
	token, _ := manager.Token(first)
	if len(scope) != 32 || scope == token || manager.NoticeScope(first) != scope {
		t.Fatal("notice scope is not stable and separate from the credential")
	}
	independent := start(request)
	if manager.NoticeScope(independent) == scope {
		t.Fatal("independent logins share a notice scope")
	}
	second := start(first)
	if manager.NoticeScope(second) == scope || manager.NoticeScope(first) != "" {
		t.Fatal("new login did not replace the notice scope")
	}
	manager.End(httptest.NewRecorder(), second)
	if manager.NoticeScope(second) != "" {
		t.Fatal("signed-out session still has a notice scope")
	}
	manager.RevokeUser(t.Context(), principal.AccountID, principal.UserID)
	if manager.NoticeScope(independent) != "" {
		t.Fatal("revoked session still has a notice scope")
	}
}
