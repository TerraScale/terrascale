package auth_test

import (
	"net/http"
	"net/http/httptest"
	"testing"
	"time"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/features/auth"
	"terrascale.tech/dashboard/internal/platform/session"
)

func TestDemoSessionUsesOpaqueSecureHttpOnlyCookie(t *testing.T) {
	store := core.NewStore()
	passwordHash, err := auth.HashPassword("a sufficiently long password")
	if err != nil {
		t.Fatalf("HashPassword() error = %v", err)
	}
	account, err := store.CreateAccount(core.User{
		Email: "secure@example.test", Name: "Secure User", PasswordHash: passwordHash,
	})
	if err != nil {
		t.Fatalf("CreateAccount() error = %v", err)
	}

	sessions := session.New(store, session.Options{
		CookieName: "ts_session", Secure: true, Lifetime: time.Hour,
	})
	request := httptest.NewRequest("POST", "/api/v1/auth/password/login", nil)
	response := httptest.NewRecorder()
	if err := sessions.Start(response, request, account.Principal()); err != nil {
		t.Fatalf("Sessions.Start() error = %v", err)
	}
	cookies := response.Result().Cookies()
	if len(cookies) != 1 {
		t.Fatalf("session cookie count = %d, want 1", len(cookies))
	}
	cookie := cookies[0]
	if cookie.Name != "ts_session" || cookie.Value == "" || cookie.Value == account.ID || cookie.Value == account.User.ID {
		t.Fatalf("session cookie is not an opaque account-independent token: %#v", cookie)
	}
	if !cookie.HttpOnly || !cookie.Secure || cookie.SameSite != http.SameSiteLaxMode || cookie.Path != "/" || cookie.MaxAge <= 0 {
		t.Fatalf("session cookie security attributes are incomplete: %#v", cookie)
	}

	request.AddCookie(cookie)
	principal, ok := sessions.Principal(request)
	if !ok || principal.AccountID != account.ID || principal.UserID != account.User.ID {
		t.Fatalf("session principal = (%#v, %t), want the registered account", principal, ok)
	}
}
