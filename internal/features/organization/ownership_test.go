package organization

import (
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"testing"
	"time"

	"terrascale.tech/dashboard/internal/core"
)

// sharedOrganization returns an owner whose organization has one more active
// member, the principal of that member, and the organization's service.
func sharedOrganization(t *testing.T) (Service, core.Principal, core.Principal) {
	t.Helper()
	store := core.NewStore()
	service := NewService(store)
	ownerAccount, err := store.CreateAccount(core.User{Name: "Olivia Owner", Email: "owner@example.test", PasswordHash: "hash"})
	if err != nil {
		t.Fatal(err)
	}
	owner := ownerAccount.Principal()
	_, token, err := service.CreateInvitation(owner, "member@example.test", roleMember)
	if err != nil {
		t.Fatal(err)
	}
	memberAccount, err := store.CreateAccount(core.User{Name: "Max Member", Email: "member@example.test", PasswordHash: "hash"})
	if err != nil {
		t.Fatal(err)
	}
	if _, err := store.AcceptInvitation(memberAccount.Principal(), hashToken(token)); err != nil {
		t.Fatal(err)
	}
	member, ok := store.CurrentPrincipal(memberAccount.ID, memberAccount.User.ID)
	if !ok || member.OrganizationID != owner.OrganizationID {
		t.Fatalf("member principal = %+v, want organization %s", member, owner.OrganizationID)
	}
	return service, owner, member
}

func TestOwnershipTransferCanBeCanceledOrDeclined(t *testing.T) {
	for _, tc := range []struct {
		name   string
		cancel func(owner, member core.Principal) core.Principal
	}{
		{name: "owner cancels", cancel: func(owner, _ core.Principal) core.Principal { return owner }},
		{name: "nominee declines", cancel: func(_, member core.Principal) core.Principal { return member }},
	} {
		t.Run(tc.name, func(t *testing.T) {
			service, owner, member := sharedOrganization(t)
			if err := service.RequestOwnershipTransfer(owner, member.UserID); err != nil {
				t.Fatal(err)
			}
			if err := service.CancelOwnershipTransfer(tc.cancel(owner, member)); err != nil {
				t.Fatal(err)
			}
			if err := service.ConfirmOwnershipTransfer(member); !errors.Is(err, ErrNoTransfer) {
				t.Fatalf("confirming a withdrawn transfer = %v, want %v", err, ErrNoTransfer)
			}
			if err := service.CancelOwnershipTransfer(owner); !errors.Is(err, ErrNoTransfer) {
				t.Fatalf("canceling twice = %v, want %v", err, ErrNoTransfer)
			}
			_, org, err := service.Snapshot(owner)
			if err != nil {
				t.Fatal(err)
			}
			if org.OwnerUserID != owner.UserID || org.PendingOwnerUserID != "" {
				t.Fatalf("organization owner %s, pending %q; want %s and none", org.OwnerUserID, org.PendingOwnerUserID, owner.UserID)
			}
		})
	}
}

func TestOwnershipTransferPageData(t *testing.T) {
	service, owner, member := sharedOrganization(t)
	if err := service.RequestOwnershipTransfer(owner, member.UserID); err != nil {
		t.Fatal(err)
	}
	now := time.Now()
	ownerAccount, org, err := service.Snapshot(owner)
	if err != nil {
		t.Fatal(err)
	}
	ownerPage := makeOrganizationPage(ownerAccount, org, owner, "", "", now)
	if ownerPage.PendingOwner.ID != member.UserID || ownerPage.PendingOwner.Name != "Max Member" || ownerPage.TransferExpires == "" || ownerPage.CanConfirmOwnership {
		t.Fatalf("owner page = pending %+v until %q, can confirm %t", ownerPage.PendingOwner, ownerPage.TransferExpires, ownerPage.CanConfirmOwnership)
	}
	memberAccount, memberOrg, err := service.Snapshot(member)
	if err != nil {
		t.Fatal(err)
	}
	memberPage := makeOrganizationPage(memberAccount, memberOrg, member, "", "", now)
	if !memberPage.CanConfirmOwnership || memberPage.OwnerName != "Olivia Owner" {
		t.Fatalf("nominee page = can confirm %t, asked by %q", memberPage.CanConfirmOwnership, memberPage.OwnerName)
	}
	if expired := makeOrganizationPage(ownerAccount, org, owner, "", "", now.Add(8*24*time.Hour)); expired.PendingOwner.ID != "" {
		t.Fatalf("an expired transfer still shows as pending: %+v", expired.PendingOwner)
	}
}

func hashToken(token string) string {
	digest := sha256.Sum256([]byte(token))
	return hex.EncodeToString(digest[:])
}
