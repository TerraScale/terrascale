package storetest

import (
	"testing"

	"terrascale.tech/dashboard/internal/core"
)

// TestSubjectIdentity checks FindBySubject and that a ts-auth subject names
// one account.
func TestSubjectIdentity(t *testing.T, s core.Store) {
	a, err := s.CreateAccount(ctx, core.User{Name: "Linked", Email: "linked@example.test", PasswordHash: "hash", AuthSubject: "sub-1"})
	want(t, err, nil)
	got, err := s.FindBySubject(ctx, "sub-1")
	want(t, err, nil)
	if got.ID != a.ID || got.User.AuthSubject != "sub-1" {
		t.Fatalf("FindBySubject(sub-1) = %q with subject %q, want %q", got.ID, got.User.AuthSubject, a.ID)
	}
	for _, subject := range []string{"", "sub-unknown"} {
		_, err := s.FindBySubject(ctx, subject)
		want(t, err, core.ErrNotFound)
	}
	_, err = s.CreateAccount(ctx, core.User{Name: "Twin", Email: "twin@example.test", PasswordHash: "hash", AuthSubject: "sub-1"})
	want(t, err, core.ErrConflict)
}

// TestSubjectLinksOnce checks that Update links an unlinked account to a free
// subject once and never relinks it.
func TestSubjectLinksOnce(t *testing.T, s core.Store) {
	linked, err := s.CreateAccount(ctx, core.User{Name: "Linked", Email: "linked@example.test", PasswordHash: "hash", AuthSubject: "sub-1"})
	want(t, err, nil)
	a := account(t, s, "unlinked@example.test")
	link := func(subject string) error {
		return s.Update(ctx, a.ID, func(a *core.Account) error {
			a.User.AuthSubject = subject
			return nil
		})
	}
	want(t, link("sub-1"), core.ErrConflict)
	if got, err := s.FindBySubject(ctx, "sub-1"); err != nil || got.ID != linked.ID {
		t.Fatalf("FindBySubject(sub-1) after a rejected link = %q, %v; want %q", got.ID, err, linked.ID)
	}
	want(t, link("sub-2"), nil)
	if got, err := s.FindBySubject(ctx, "sub-2"); err != nil || got.ID != a.ID {
		t.Fatalf("FindBySubject(sub-2) = %q, %v; want %q", got.ID, err, a.ID)
	}
	want(t, link("sub-3"), core.ErrInvalid)
}
