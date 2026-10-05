package access

import (
	"context"
	"errors"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/core/memstore"
	"terrascale.tech/dashboard/internal/platform/csrf"
	"terrascale.tech/dashboard/internal/platform/session"
)

type invitationSessionStore struct {
	session.Store
	err error
}

func (s invitationSessionStore) Delete(ctx context.Context, key [32]byte) error {
	if s.err != nil {
		return s.err
	}
	return s.Store.Delete(ctx, key)
}

func TestSwitchInvitationAccountRetriesFailedLogoutBeforeRedirecting(t *testing.T) {
	store := memstore.New()
	account, err := store.CreateAccount(t.Context(), core.User{Name: "Owner", Email: "switch@example.test", PasswordHash: "unused"})
	if err != nil {
		t.Fatal(err)
	}
	state := &invitationSessionStore{Store: session.NewMemoryStore()}
	sessions := session.New(store, session.Options{Store: state})
	guard := csrf.New(sessions, csrf.Options{})
	start := httptest.NewRecorder()
	initial := httptest.NewRequest(http.MethodGet, "/", nil)
	if err := sessions.Start(start, initial, account.Principal()); err != nil {
		t.Fatal(err)
	}
	token := guard.Token(start, initial)
	request := httptest.NewRequest(http.MethodPost, "/invitations/invite-token/switch-account", nil)
	request.SetPathValue("token", "invite-token")
	request.Header.Set("X-CSRF-Token", token)
	for _, cookie := range start.Result().Cookies() {
		request.AddCookie(cookie)
	}
	handler := switchInvitationAccount(core.Deps{Sessions: sessions, CSRF: guard})
	state.err = errors.New("session database down")
	response := httptest.NewRecorder()
	handler(response, request)
	if response.Code != http.StatusServiceUnavailable || response.Header().Get("Location") != "" || len(response.Result().Cookies()) != 0 || !strings.Contains(response.Body.String(), "Retry switching accounts.") {
		t.Fatalf("failed switch = %d %v %q", response.Code, response.Header(), response.Body.String())
	}
	if _, valid := sessions.Principal(request); !valid {
		t.Fatal("session was unexpectedly revoked on failed deletion")
	}
	state.err = nil
	response = httptest.NewRecorder()
	handler(response, request)
	if response.Code != http.StatusSeeOther || response.Header().Get("Location") != invitationLoginURL("invite-token") {
		t.Fatalf("switch retry = %d %v", response.Code, response.Header())
	}
	if _, valid := sessions.Principal(request); valid {
		t.Fatal("session remains valid after successful account switch")
	}
}
