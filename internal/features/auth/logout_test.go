package auth

import (
	"context"
	"errors"
	"net/http"
	"net/http/httptest"
	"testing"
	"time"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/core/memstore"
	"terrascale.tech/dashboard/internal/platform/session"
)

type faultSessionStore struct {
	session.Store
	getErr, deleteErr error
	cancel            context.CancelFunc
}

func (s *faultSessionStore) Get(ctx context.Context, key [32]byte, now time.Time) (session.Record, error) {
	if s.cancel != nil {
		s.cancel()
	}
	if err := ctx.Err(); err != nil {
		return session.Record{}, err
	}
	if s.getErr != nil {
		return session.Record{}, s.getErr
	}
	return s.Store.Get(ctx, key, now)
}

func (s *faultSessionStore) Delete(ctx context.Context, key [32]byte) error {
	if s.deleteErr != nil {
		return s.deleteErr
	}
	return s.Store.Delete(ctx, key)
}

func TestLogoutDoesNotReportSuccessWhenRevocationFails(t *testing.T) {
	for _, mode := range []string{"cookie", "bearer", "both"} {
		t.Run(mode, func(t *testing.T) {
			store := memstore.New()
			account, err := store.CreateAccount(t.Context(), core.User{Name: "Owner", Email: "logout@example.test", PasswordHash: "unused"})
			if err != nil {
				t.Fatal(err)
			}
			state := &faultSessionStore{Store: session.NewMemoryStore()}
			sessions := session.New(store, session.Options{Store: state})
			start := httptest.NewRecorder()
			if err := sessions.Start(start, httptest.NewRequest(http.MethodGet, "/", nil), account.Principal()); err != nil {
				t.Fatal(err)
			}
			cookie := start.Result().Cookies()[0]
			request := httptest.NewRequest(http.MethodPost, "/logout", nil)
			if mode != "bearer" {
				request.AddCookie(cookie)
			}
			if mode != "cookie" {
				request.Header.Set("Authorization", "Bearer "+cookie.Value)
			}
			state.deleteErr = errors.New("session database down")
			response := httptest.NewRecorder()
			logout(core.Deps{Sessions: sessions})(response, request)
			if response.Code != http.StatusServiceUnavailable || response.Header().Get("Location") != "" || len(response.Result().Cookies()) != 0 {
				t.Fatalf("failed revocation = %d, headers %v; want 503 without redirect or cleared cookie", response.Code, response.Header())
			}
			copy := httptest.NewRequest(http.MethodGet, "/", nil)
			copy.Header.Set("Authorization", "Bearer "+cookie.Value)
			if _, valid := sessions.BearerPrincipal(copy); !valid {
				t.Fatal("fixture token was unexpectedly revoked on the failed deletion")
			}
			state.deleteErr = nil
			response = httptest.NewRecorder()
			logout(core.Deps{Sessions: sessions})(response, request)
			if response.Code != http.StatusSeeOther || response.Header().Get("Location") != "/login" {
				t.Fatalf("retry logout = %d, headers %v", response.Code, response.Header())
			}
			if _, valid := sessions.BearerPrincipal(copy); valid {
				t.Fatal("copied token still authenticates after successful logout")
			}
		})
	}
}
