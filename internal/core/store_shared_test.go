package core

import (
	"errors"
	"testing"
)

// sharedOrganization returns an owner whose Personal organization has a
// second project, and an invitation token for guestEmail to join it.
func sharedOrganization(t *testing.T, store *Store, guestEmail string) (owner Principal, extraProjectID, token string) {
	t.Helper()
	account, err := store.CreateAccount(User{Name: "Owner", Email: "owner@example.test", PasswordHash: "hash"})
	if err != nil {
		t.Fatal(err)
	}
	owner = account.Principal()
	extraProjectID, token = NewID("prj"), NewID("tok")
	if err := store.UpdateAs(owner, func(account *Account) error {
		org := account.Organizations[owner.OrganizationID]
		org.Projects[extraProjectID] = Project{ID: extraProjectID, OrganizationID: org.ID, Name: "Extra", Status: "active"}
		org.Invitations[NewID("inv")] = Invitation{Email: guestEmail, Role: "member", Status: "pending", TokenHash: token}
		account.Organizations[org.ID] = org
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	return owner, extraProjectID, token
}

func TestAcceptInvitationSelectsDefaultProject(t *testing.T) {
	store := NewStore()
	owner, _, token := sharedOrganization(t, store, "guest@example.test")
	guest, err := store.CreateAccount(User{Name: "Guest", Email: "guest@example.test", PasswordHash: "hash"})
	if err != nil {
		t.Fatal(err)
	}
	if _, err := store.AcceptInvitation(guest.Principal(), token); err != nil {
		t.Fatal(err)
	}
	p, ok := store.CurrentPrincipal(guest.ID, guest.User.ID)
	if !ok || p.OrganizationID != owner.OrganizationID || p.ProjectID != owner.ProjectID {
		t.Fatalf("guest principal = %+v, want organization %s and its default project %s", p, owner.OrganizationID, owner.ProjectID)
	}
}

func TestAcceptInvitationRejectsOrganizationPendingDeletion(t *testing.T) {
	store := NewStore()
	owner, _, token := sharedOrganization(t, store, "guest@example.test")
	if err := store.UpdateAs(owner, func(account *Account) error {
		org := account.Organizations[owner.OrganizationID]
		org.DeletionRequested = true
		account.Organizations[org.ID] = org
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	guest, err := store.CreateAccount(User{Name: "Guest", Email: "guest@example.test", PasswordHash: "hash"})
	if err != nil {
		t.Fatal(err)
	}
	if _, err := store.AcceptInvitation(guest.Principal(), token); !errors.Is(err, ErrConflict) {
		t.Fatalf("AcceptInvitation = %v, want %v", err, ErrConflict)
	}
}

func TestDeletedProjectIsDeselectedForOtherMembers(t *testing.T) {
	store := NewStore()
	owner, extraProjectID, token := sharedOrganization(t, store, "guest@example.test")
	guest, err := store.CreateAccount(User{Name: "Guest", Email: "guest@example.test", PasswordHash: "hash"})
	if err != nil {
		t.Fatal(err)
	}
	if _, err := store.AcceptInvitation(guest.Principal(), token); err != nil {
		t.Fatal(err)
	}
	if err := store.Update(guest.ID, func(account *Account) error {
		account.ActiveProjectID = extraProjectID
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	if err := store.UpdateAs(owner, func(account *Account) error {
		delete(account.Organizations[owner.OrganizationID].Projects, extraProjectID)
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	p, ok := store.SessionPrincipal(guest.ID, guest.User.ID, 0)
	if !ok || p.ProjectID != owner.ProjectID {
		t.Fatalf("guest session = %+v (%t), want the default project %s", p, ok, owner.ProjectID)
	}
}

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
