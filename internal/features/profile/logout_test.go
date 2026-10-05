package profile

import (
	"context"
	"errors"
	"net/http"
	"net/http/httptest"
	"net/url"
	"strings"
	"testing"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/core/memstore"
	"terrascale.tech/dashboard/internal/features/auth"
	"terrascale.tech/dashboard/internal/platform/session"
)

type passwordConflictStore struct {
	core.Store
	conflict bool
}

func (s passwordConflictStore) UpdateAs(ctx context.Context, p core.Principal, mutate func(*core.Account) error) error {
	if s.conflict {
		return core.ErrConflict
	}
	return s.Store.UpdateAs(ctx, p, mutate)
}

type passwordSessionStore struct {
	session.Store
	err error
}

func (s passwordSessionStore) Delete(ctx context.Context, key [32]byte) error {
	if s.err != nil {
		return s.err
	}
	return s.Store.Delete(ctx, key)
}

func (s passwordSessionStore) DeleteUser(ctx context.Context, accountID, userID string) error {
	if s.err != nil {
		return s.err
	}
	return s.Store.DeleteUser(ctx, accountID, userID)
}

func TestPasswordChangeReportsWhetherCommitSucceededWhenLogoutFails(t *testing.T) {
	hash, err := auth.HashPassword("correct-password")
	if err != nil {
		t.Fatal(err)
	}
	for _, conflict := range []bool{false, true} {
		for _, deletionFails := range []bool{false, true} {
			name := "updated"
			if conflict {
				name = "conflict"
			}
			if deletionFails {
				name += "/logout-failed"
			} else {
				name += "/logged-out"
			}
			t.Run(name, func(t *testing.T) {
				base := memstore.New()
				account, err := base.CreateAccount(t.Context(), core.User{Name: "Owner", Email: "password@example.test", PasswordHash: hash})
				if err != nil {
					t.Fatal(err)
				}
				store := passwordConflictStore{Store: base, conflict: conflict}
				state := &passwordSessionStore{Store: session.NewMemoryStore()}
				sessions := session.New(store, session.Options{Store: state})
				start := httptest.NewRecorder()
				if err := sessions.Start(start, httptest.NewRequest(http.MethodGet, "/", nil), account.Principal()); err != nil {
					t.Fatal(err)
				}
				request := httptest.NewRequest(http.MethodPost, "/api/v1/dashboard/profile/password", strings.NewReader(url.Values{
					"currentPassword": {"correct-password"}, "newPassword": {"new-password-123"}, "confirmPassword": {"new-password-123"},
				}.Encode()))
				request.Header.Set("Content-Type", "application/x-www-form-urlencoded")
				request.AddCookie(start.Result().Cookies()[0])
				if deletionFails {
					state.err = errors.New("session database down")
				}
				response := httptest.NewRecorder()
				changePassword(core.Deps{Store: store, Sessions: sessions})(response, request)
				if deletionFails {
					message := "Password updated; sign-out could not finish. Retry logout."
					if conflict {
						message = "Your account changed; sign-out could not finish. Retry logout."
					}
					if response.Code != http.StatusServiceUnavailable || response.Header().Get("Location") != "" || len(response.Result().Cookies()) != 0 || !strings.Contains(response.Body.String(), message) {
						t.Fatalf("failed sign-out = %d %v %q", response.Code, response.Header(), response.Body.String())
					}
				} else if response.Code != http.StatusSeeOther || response.Header().Get("Location") != "/login" {
					t.Fatalf("sign-out = %d %v", response.Code, response.Header())
				}
				current, err := base.FindByEmail(t.Context(), account.User.Email)
				if err != nil {
					t.Fatal(err)
				}
				if conflict {
					if current.User.PasswordHash != hash || current.AuthVersion != account.AuthVersion {
						t.Fatal("conflicting password change modified the credential")
					}
				} else if !auth.VerifyPassword(current.User.PasswordHash, "new-password-123") || current.AuthVersion <= account.AuthVersion {
					t.Fatal("successful password change did not commit and invalidate old credentials")
				}
				if _, valid := sessions.Principal(request); valid != (conflict && deletionFails) {
					t.Fatalf("old session valid = %t, conflict = %t, deletionFails = %t", valid, conflict, deletionFails)
				}
			})
		}
	}
}
