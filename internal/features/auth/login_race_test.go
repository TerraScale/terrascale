package auth

import (
	"io/fs"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/platform/session"
)

func TestPasswordChangeDuringLoginDoesNotIssueFailedSession(t *testing.T) {
	oldHash, err := HashPassword("the original sufficiently long password")
	if err != nil {
		t.Fatal(err)
	}
	newHash, err := HashPassword("the replacement sufficiently long password")
	if err != nil {
		t.Fatal(err)
	}
	store := core.NewStore()
	account, err := store.CreateAccount(core.User{
		Email: "race@example.test", Name: "Race User", PasswordHash: oldHash,
	})
	if err != nil {
		t.Fatal(err)
	}

	manager := session.New(store, session.Options{})
	sessions := passwordChangeBeforeIssue{
		Sessions: manager, CredentialSessions: manager,
		store: store, replacementHash: newHash,
	}
	mux := http.NewServeMux()
	Mount(mux, core.Deps{Store: store, Sessions: sessions, Render: loginTestRenderer{}})
	request := httptest.NewRequest(http.MethodPost, "/api/v1/auth/password/login", strings.NewReader(
		`{"email":"race@example.test","password":"the original sufficiently long password"}`,
	))
	request.Header.Set("Content-Type", "application/json")
	response := httptest.NewRecorder()
	mux.ServeHTTP(response, request)

	if response.Code != http.StatusUnauthorized {
		t.Fatalf("login status = %d, want 401; body=%s", response.Code, response.Body.String())
	}
	if cookies := response.Result().Cookies(); len(cookies) != 0 {
		t.Fatalf("failed login set cookies: %#v", cookies)
	}
	current, ok := store.Snapshot(account.ID)
	if !ok || current.User.PasswordHash != newHash || current.AuthVersion != account.AuthVersion+1 {
		t.Fatalf("password change did not land during login: account=%+v", current)
	}
}

type passwordChangeBeforeIssue struct {
	core.Sessions
	core.CredentialSessions
	store           *core.Store
	replacementHash string
}

func (s passwordChangeBeforeIssue) StartIfPasswordHash(w http.ResponseWriter, r *http.Request, p core.Principal, expectedHash string) error {
	if err := s.store.Update(p.AccountID, func(account *core.Account) error {
		account.User.PasswordHash = s.replacementHash
		return nil
	}); err != nil {
		return err
	}
	return s.CredentialSessions.StartIfPasswordHash(w, r, p, expectedHash)
}

type loginTestRenderer struct{}

func (loginTestRenderer) Register(string, fs.FS, ...string) error { return nil }
func (loginTestRenderer) Page(http.ResponseWriter, *http.Request, core.Page) error {
	return nil
}
func (loginTestRenderer) Fragment(http.ResponseWriter, *http.Request, core.Fragment) error {
	return nil
}
