package profile

import (
	"context"
	"fmt"
	"net/http"
	"net/http/httptest"
	"net/url"
	"reflect"
	"strings"
	"testing"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/core/memstore"
	"terrascale.tech/dashboard/internal/features/auth"
	"terrascale.tech/dashboard/internal/platform/session"
)

var outage = fmt.Errorf("load: %w", core.ErrUnavailable)

func TestProfileErrorsReportStoreOutageAsUnavailable(t *testing.T) {
	update := httptest.NewRecorder()
	writeUpdateError(update, outage)
	tokens := httptest.NewRecorder()
	writeAccessTokenError(tokens, httptest.NewRequest(http.MethodPost, "/api/v1/dashboard/profile/access-tokens", nil), outage)
	if update.Code != http.StatusServiceUnavailable || tokens.Code != http.StatusServiceUnavailable {
		t.Fatalf("update = %d, tokens = %d, want 503", update.Code, tokens.Code)
	}
}

type snapshotFailureStore struct {
	core.Store
	err   error
	reads int
}

func (s *snapshotFailureStore) SnapshotAs(ctx context.Context, p core.Principal) (core.Account, error) {
	s.reads++
	if s.err != nil {
		return core.Account{}, s.err
	}
	return s.Store.SnapshotAs(ctx, p)
}

func TestProfileSnapshotHandlersPreserveOutagesAndAuthFailures(t *testing.T) {
	base := memstore.New()
	hash, err := auth.HashPassword("correct-password")
	if err != nil {
		t.Fatal(err)
	}
	account, err := base.CreateAccount(t.Context(), core.User{Name: "Owner", Email: "owner@example.test", PasswordHash: hash})
	if err != nil {
		t.Fatal(err)
	}
	store := &snapshotFailureStore{Store: base}
	sessions := session.New(store, session.Options{})
	mux := http.NewServeMux()
	Mount(mux, core.Deps{Store: store, Sessions: sessions, Tools: core.NewTools(store), Demo: true})
	signIn := httptest.NewRecorder()
	if err := sessions.Start(signIn, httptest.NewRequest(http.MethodGet, "/", nil), account.Principal()); err != nil {
		t.Fatal(err)
	}
	form := url.Values{
		"password": {"wrong-password"}, "currentPassword": {"wrong-password"},
		"newPassword": {"new-password-123"}, "confirmPassword": {"new-password-123"},
		"newEmail": {"new@example.test"}, "name": {"Assistant"},
	}
	for _, failure := range []struct {
		name   string
		err    error
		status int
	}{
		{"unavailable", outage, http.StatusServiceUnavailable},
		{"not-found", fmt.Errorf("load: %w", core.ErrNotFound), http.StatusUnauthorized},
		{"forbidden", fmt.Errorf("load: %w", core.ErrForbidden), http.StatusUnauthorized},
		{"unauthorized", fmt.Errorf("load: %w", core.ErrUnauthorized), http.StatusUnauthorized},
		{"incorrect-password", nil, http.StatusSeeOther},
	} {
		for _, route := range []struct {
			name   string
			method string
			path   string
		}{
			{"page", http.MethodGet, "/profile"},
			{"access-token", http.MethodPost, "/api/v1/dashboard/profile/access-tokens"},
			{"password", http.MethodPost, "/api/v1/dashboard/profile/password"},
			{"email", http.MethodPost, "/api/v1/dashboard/profile/email-change"},
		} {
			if failure.err == nil && route.method == http.MethodGet {
				continue
			}
			t.Run(failure.name+"/"+route.name, func(t *testing.T) {
				store.err, store.reads = failure.err, 0
				request := httptest.NewRequest(route.method, route.path, strings.NewReader(form.Encode()))
				request.Header.Set("Content-Type", "application/x-www-form-urlencoded")
				for _, cookie := range signIn.Result().Cookies() {
					request.AddCookie(cookie)
				}
				response := httptest.NewRecorder()
				mux.ServeHTTP(response, request)
				status := failure.status
				if failure.err == nil && route.name == "access-token" {
					status = http.StatusBadRequest
				}
				if response.Code != status || store.reads != 1 {
					t.Fatalf("response = %d %s, reads = %d; want %d and one snapshot read", response.Code, response.Body.String(), store.reads, status)
				}
				if failure.err == nil {
					message := response.Body.String()
					if status == http.StatusSeeOther {
						location, err := url.Parse(response.Header().Get("Location"))
						if err != nil || location.Path != "/profile" {
							t.Fatalf("password error redirect = %q: %v", response.Header().Get("Location"), err)
						}
						message = location.Query().Get("error")
					}
					if !strings.Contains(message, "Current password is incorrect.") {
						t.Fatalf("password error = %q", message)
					}
				}
				current, err := base.SnapshotAs(t.Context(), account.Principal())
				if err != nil || !reflect.DeepEqual(current, account) {
					t.Fatalf("failed request changed the account: %v", err)
				}
			})
		}
	}
}
