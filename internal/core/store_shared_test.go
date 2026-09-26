package core

import (
	"errors"
	"testing"
)

func TestAuthorizePlatformAdmin(t *testing.T) {
	store := NewStore()
	admin, err := store.CreateAccount(User{Name: "Admin", Email: "admin@example.test", PasswordHash: "hash", PlatformAdmin: true})
	if err != nil {
		t.Fatal(err)
	}
	member, err := store.CreateAccount(User{Name: "Member", Email: "member@example.test", PasswordHash: "hash"})
	if err != nil {
		t.Fatal(err)
	}
	stale := admin.Principal()
	if err := store.Update(admin.ID, func(account *Account) error {
		account.User.PasswordHash = "rotated"
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	current, _ := store.CurrentPrincipal(admin.ID, admin.User.ID)
	for _, tc := range []struct {
		name      string
		principal Principal
		want      error
	}{
		{name: "administrator", principal: current},
		{name: "member", principal: member.Principal(), want: ErrForbidden},
		{name: "revoked credential", principal: stale, want: ErrUnauthorized},
		{name: "unknown account", principal: Principal{AccountID: "acct_missing"}, want: ErrNotFound},
	} {
		if err := store.AuthorizePlatformAdmin(tc.principal); !errors.Is(err, tc.want) {
			t.Errorf("%s: AuthorizePlatformAdmin = %v, want %v", tc.name, err, tc.want)
		}
	}
}
