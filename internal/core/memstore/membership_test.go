package memstore

import (
	"crypto/sha256"
	"encoding/hex"
	"testing"
	"time"

	"terrascale.tech/dashboard/internal/core"
)

// joinedMember returns a Personal account that also belongs to owner's
// organization through an accepted invitation, with that organization
// selected, and the owner's principal.
func joinedMember(t *testing.T, store *Store) (member core.Account, owner core.Principal) {
	t.Helper()
	ownerAccount, err := store.CreateAccount(ctx, core.User{Name: "Owner", Email: "owner@example.test", PasswordHash: "unused"})
	if err != nil {
		t.Fatal(err)
	}
	owner = ownerAccount.Principal()
	digest := sha256.Sum256([]byte("invitation-token"))
	if err := store.UpdateAs(ctx, owner, func(a *core.Account) error {
		org := a.Organizations[owner.OrganizationID]
		org.Invitations["inv_member"] = core.Invitation{
			ID: "inv_member", Email: "member@example.test", Role: "member", Status: "pending",
			TokenHash: hex.EncodeToString(digest[:]), ExpiresAt: time.Now().Add(time.Hour),
		}
		a.Organizations[org.ID] = org
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	member, err = store.CreateAccount(ctx, core.User{Name: "Old Name", Email: "member@example.test", PasswordHash: "unused"})
	if err != nil {
		t.Fatal(err)
	}
	if _, err := store.AcceptInvitation(ctx, member.Principal(), hex.EncodeToString(digest[:])); err != nil {
		t.Fatal(err)
	}
	if member, err = store.FindByEmail(ctx, "member@example.test"); err != nil {
		t.Fatal(err)
	}
	return member, owner
}

func memberName(t *testing.T, store *Store, p core.Principal, userID string) string {
	t.Helper()
	a, err := store.SnapshotAs(ctx, p)
	if err != nil {
		t.Fatal(err)
	}
	return a.Organizations[p.OrganizationID].Members[userID].Name
}

func TestRenameReachesEveryOrganizationOfTheUser(t *testing.T) {
	store := New()
	member, owner := joinedMember(t, store)
	var personal core.Principal
	for id, org := range member.Organizations {
		if org.Kind == "personal" {
			personal = member.Principal()
			personal.OrganizationID, personal.ProjectID = id, ""
		}
	}

	if err := store.UpdateAs(ctx, personal, func(a *core.Account) error {
		a.User.Name = "New Name"
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	if got := memberName(t, store, owner, member.User.ID); got != "New Name" {
		t.Errorf("member name in the other organization = %q, want New Name", got)
	}
	if got := memberName(t, store, personal, member.User.ID); got != "New Name" {
		t.Errorf("member name in Personal = %q, want New Name", got)
	}
}

func TestAdminRenameReachesEveryOrganizationOfTheUser(t *testing.T) {
	store := New()
	member, owner := joinedMember(t, store)
	admin, err := store.CreateAccount(ctx, core.User{Name: "Admin", Email: "admin@example.test", PasswordHash: "unused", PlatformAdmin: true})
	if err != nil {
		t.Fatal(err)
	}
	if err := store.UpdateAccountAsAdmin(ctx, admin.Principal(), member.ID, func(a *core.Account) error {
		a.User.Name = "Admin Renamed"
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	if got := memberName(t, store, owner, member.User.ID); got != "Admin Renamed" {
		t.Errorf("member name in the other organization = %q, want Admin Renamed", got)
	}
}

func TestChangeEmailReachesEveryOrganizationOfTheUser(t *testing.T) {
	store := New()
	member, owner := joinedMember(t, store)
	if err := store.ChangeEmailAs(ctx, member.Principal(), "renamed@example.test", "unused"); err != nil {
		t.Fatal(err)
	}
	a, err := store.SnapshotAs(ctx, owner)
	if err != nil {
		t.Fatal(err)
	}
	if got := a.Organizations[owner.OrganizationID].Members[member.User.ID].Email; got != "renamed@example.test" {
		t.Errorf("member email in the other organization = %q, want renamed@example.test", got)
	}
}
