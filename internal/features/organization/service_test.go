package organization

import (
	"context"
	"errors"
	"fmt"
	"testing"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/core/memstore"
)

func TestCreateInvitationReportsTheMemberLimit(t *testing.T) {
	store := memstore.New()
	account, err := store.CreateAccount(t.Context(), core.User{Name: "Owner", Email: "owner@example.test", PasswordHash: "hash"})
	if err != nil {
		t.Fatal(err)
	}
	owner := account.Principal()
	if err := store.UpdateAs(t.Context(), owner, func(a *core.Account) error {
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
		if _, _, err := service.CreateInvitation(t.Context(), owner, fmt.Sprintf("user%d@example.test", i), roleMember); err != nil {
			t.Fatalf("invitation %d: %v", i, err)
		}
	}
	if _, _, err := service.CreateInvitation(t.Context(), owner, "user0@example.test", roleMember); !errors.Is(err, core.ErrConflict) {
		t.Fatalf("duplicate invitation = %v, want %v", err, core.ErrConflict)
	}
	if _, _, err := service.CreateInvitation(t.Context(), owner, "sixth@example.test", roleMember); !errors.Is(err, ErrMemberLimit) {
		t.Fatalf("invitation past the limit = %v, want %v", err, ErrMemberLimit)
	}
}

// failingSessions is a session store whose revocation always fails.
type failingSessions struct{ core.Sessions }

func (failingSessions) RevokeUser(context.Context, string, string) error {
	return fmt.Errorf("%w: session store down", core.ErrUnavailable)
}

func TestRemoveMemberSucceedsWhenSessionRevocationFails(t *testing.T) {
	service, owner, member := sharedOrganization(t)
	service.sessions = failingSessions{}
	if err := service.RemoveMember(t.Context(), owner, member.UserID); err != nil {
		t.Fatalf("remove member with failing revocation = %v, want nil", err)
	}
	_, org, err := service.Snapshot(t.Context(), owner)
	if err != nil {
		t.Fatal(err)
	}
	if _, ok := org.Members[member.UserID]; ok {
		t.Fatalf("member %s is still in the organization", member.UserID)
	}
}
