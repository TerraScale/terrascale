package organization

import (
	"errors"
	"fmt"
	"testing"

	"terrascale.tech/dashboard/internal/core"
)

func TestCreateInvitationReportsTheMemberLimit(t *testing.T) {
	store := core.NewStore()
	account, err := store.CreateAccount(core.User{Name: "Owner", Email: "owner@example.test", PasswordHash: "hash"})
	if err != nil {
		t.Fatal(err)
	}
	owner := account.Principal()
	if err := store.UpdateAs(owner, func(a *core.Account) error {
		org := a.Organizations[owner.OrganizationID]
		org.PlanID = "plan_starter"
		a.Organizations[org.ID] = org
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	service := NewService(store, nil)
	// Starter seats five: the owner and four pending invitations.
	for i := range 4 {
		if _, _, err := service.CreateInvitation(owner, fmt.Sprintf("user%d@example.test", i), roleMember); err != nil {
			t.Fatalf("invitation %d: %v", i, err)
		}
	}
	if _, _, err := service.CreateInvitation(owner, "user0@example.test", roleMember); !errors.Is(err, core.ErrConflict) {
		t.Fatalf("duplicate invitation = %v, want %v", err, core.ErrConflict)
	}
	if _, _, err := service.CreateInvitation(owner, "sixth@example.test", roleMember); !errors.Is(err, ErrMemberLimit) {
		t.Fatalf("invitation past the limit = %v, want %v", err, ErrMemberLimit)
	}
}
