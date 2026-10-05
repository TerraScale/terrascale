package storetest

import (
	"errors"
	"testing"

	"terrascale.tech/dashboard/internal/core"
)

// sharedOrganization returns an owner whose Personal organization has a
// second project, and an invitation token for guestEmail to join it.
func sharedOrganization(t *testing.T, store core.Store, guestEmail string) (owner core.Principal, extraProjectID, token string) {
	t.Helper()
	account, err := store.CreateAccount(ctx, core.User{Name: "Owner", Email: "owner@example.test", PasswordHash: "hash"})
	if err != nil {
		t.Fatal(err)
	}
	owner = account.Principal()
	extraProjectID, token = core.NewID("prj"), core.NewID("tok")
	if err := store.UpdateAs(ctx, owner, func(account *core.Account) error {
		org := account.Organizations[owner.OrganizationID]
		org.Projects[extraProjectID] = core.Project{ID: extraProjectID, OrganizationID: org.ID, Name: "Extra", Status: "active"}
		id := core.NewID("inv")
		org.Invitations[id] = core.Invitation{ID: id, Email: guestEmail, Role: "member", Status: "pending", TokenHash: token}
		account.Organizations[org.ID] = org
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	return owner, extraProjectID, token
}

func TestAcceptInvitationSelectsDefaultProject(t *testing.T, store core.Store) {
	owner, _, token := sharedOrganization(t, store, "guest@example.test")
	guest, err := store.CreateAccount(ctx, core.User{Name: "Guest", Email: "guest@example.test", PasswordHash: "hash"})
	if err != nil {
		t.Fatal(err)
	}
	if _, err := store.AcceptInvitation(ctx, guest.Principal(), token); err != nil {
		t.Fatal(err)
	}
	p, err := store.CurrentPrincipal(ctx, guest.ID, guest.User.ID)
	if err != nil || p.OrganizationID != owner.OrganizationID || p.ProjectID != owner.ProjectID {
		t.Fatalf("guest principal = %+v, want organization %s and its default project %s", p, owner.OrganizationID, owner.ProjectID)
	}
}

func TestAcceptInvitationRejectsOrganizationPendingDeletion(t *testing.T, store core.Store) {
	owner, _, token := sharedOrganization(t, store, "guest@example.test")
	if err := store.UpdateAs(ctx, owner, func(account *core.Account) error {
		org := account.Organizations[owner.OrganizationID]
		org.DeletionRequested = true
		account.Organizations[org.ID] = org
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	guest, err := store.CreateAccount(ctx, core.User{Name: "Guest", Email: "guest@example.test", PasswordHash: "hash"})
	if err != nil {
		t.Fatal(err)
	}
	if _, err := store.AcceptInvitation(ctx, guest.Principal(), token); !errors.Is(err, core.ErrConflict) {
		t.Fatalf("AcceptInvitation = %v, want %v", err, core.ErrConflict)
	}
}

func TestDeletedProjectIsDeselectedForOtherMembers(t *testing.T, store core.Store) {
	owner, extraProjectID, token := sharedOrganization(t, store, "guest@example.test")
	guest, err := store.CreateAccount(ctx, core.User{Name: "Guest", Email: "guest@example.test", PasswordHash: "hash"})
	if err != nil {
		t.Fatal(err)
	}
	if _, err := store.AcceptInvitation(ctx, guest.Principal(), token); err != nil {
		t.Fatal(err)
	}
	if err := store.Update(ctx, guest.ID, func(account *core.Account) error {
		account.ActiveProjectID = extraProjectID
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	if err := store.UpdateAs(ctx, owner, func(account *core.Account) error {
		delete(account.Organizations[owner.OrganizationID].Projects, extraProjectID)
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	p, err := store.SessionPrincipal(ctx, guest.ID, guest.User.ID, 0)
	if err != nil || p.ProjectID != owner.ProjectID {
		t.Fatalf("guest session = %+v (%v), want the default project %s", p, err, owner.ProjectID)
	}
}

func TestAuthorizePlatformAdmin(t *testing.T, store core.Store) {
	admin, err := store.CreateAccount(ctx, core.User{Name: "Admin", Email: "admin@example.test", PasswordHash: "hash", PlatformAdmin: true})
	if err != nil {
		t.Fatal(err)
	}
	member, err := store.CreateAccount(ctx, core.User{Name: "Member", Email: "member@example.test", PasswordHash: "hash"})
	if err != nil {
		t.Fatal(err)
	}
	stale := admin.Principal()
	if err := store.Update(ctx, admin.ID, func(account *core.Account) error {
		account.User.PasswordHash = "rotated"
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	current, _ := store.CurrentPrincipal(ctx, admin.ID, admin.User.ID)
	for _, tc := range []struct {
		name      string
		principal core.Principal
		want      error
	}{
		{name: "administrator", principal: current},
		{name: "member", principal: member.Principal(), want: core.ErrForbidden},
		{name: "revoked credential", principal: stale, want: core.ErrUnauthorized},
		{name: "unknown account", principal: core.Principal{AccountID: "acct_missing"}, want: core.ErrNotFound},
	} {
		if err := store.AuthorizePlatformAdmin(ctx, tc.principal); !errors.Is(err, tc.want) {
			t.Errorf("%s: AuthorizePlatformAdmin = %v, want %v", tc.name, err, tc.want)
		}
	}
}
