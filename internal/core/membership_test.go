package core

import (
	"crypto/sha256"
	"encoding/hex"
	"testing"
	"time"
)

// joinedMember returns a Personal account that also belongs to owner's
// organization through an accepted invitation, with that organization
// selected, and the owner's principal.
func joinedMember(t *testing.T, store *Store) (member Account, owner Principal) {
	t.Helper()
	ownerAccount, err := store.CreateAccount(User{Name: "Owner", Email: "owner@example.test", PasswordHash: "unused"})
	if err != nil {
		t.Fatal(err)
	}
	owner = ownerAccount.Principal()
	digest := sha256.Sum256([]byte("invitation-token"))
	if err := store.UpdateAs(owner, func(a *Account) error {
		org := a.Organizations[owner.OrganizationID]
		org.Invitations["inv_member"] = Invitation{
			ID: "inv_member", Email: "member@example.test", Role: "member", Status: "pending",
			TokenHash: hex.EncodeToString(digest[:]), ExpiresAt: time.Now().Add(time.Hour),
		}
		a.Organizations[org.ID] = org
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	member, err = store.CreateAccount(User{Name: "Old Name", Email: "member@example.test", PasswordHash: "unused"})
	if err != nil {
		t.Fatal(err)
	}
	if _, err := store.AcceptInvitation(member.Principal(), hex.EncodeToString(digest[:])); err != nil {
		t.Fatal(err)
	}
	member, _ = store.FindByEmail("member@example.test")
	return member, owner
}

func memberName(t *testing.T, store *Store, p Principal, userID string) string {
	t.Helper()
	a, err := store.SnapshotAs(p)
	if err != nil {
		t.Fatal(err)
	}
	return a.Organizations[p.OrganizationID].Members[userID].Name
}

func TestRenameReachesEveryOrganizationOfTheUser(t *testing.T) {
	store := NewStore()
	member, owner := joinedMember(t, store)
	var personal Principal
	for id, org := range member.Organizations {
		if org.Kind == "personal" {
			personal = member.Principal()
			personal.OrganizationID, personal.ProjectID = id, ""
		}
	}

	if err := store.UpdateAs(personal, func(a *Account) error {
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
	store := NewStore()
	member, owner := joinedMember(t, store)
	admin, err := store.CreateAccount(User{Name: "Admin", Email: "admin@example.test", PasswordHash: "unused", PlatformAdmin: true})
	if err != nil {
		t.Fatal(err)
	}
	if err := store.UpdateAccountAsAdmin(admin.Principal(), member.ID, func(a *Account) error {
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
	store := NewStore()
	member, owner := joinedMember(t, store)
	if err := store.ChangeEmailAs(member.Principal(), "renamed@example.test", "unused"); err != nil {
		t.Fatal(err)
	}
	a, err := store.SnapshotAs(owner)
	if err != nil {
		t.Fatal(err)
	}
	if got := a.Organizations[owner.OrganizationID].Members[member.User.ID].Email; got != "renamed@example.test" {
		t.Errorf("member email in the other organization = %q, want renamed@example.test", got)
	}
}
