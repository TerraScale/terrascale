package organization

import (
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"testing"

	"terrascale.tech/dashboard/internal/core"
)

func TestBusinessProjectAndMemberLifecycle(t *testing.T) {
	store := core.NewStore()
	owner, err := store.CreateAccount(core.User{Name: "Owner", Email: "owner@example.test", PasswordHash: "hash"})
	if err != nil {
		t.Fatal(err)
	}
	member, err := store.CreateAccount(core.User{Name: "Member", Email: "member@example.test", PasswordHash: "hash"})
	if err != nil {
		t.Fatal(err)
	}
	service := NewService(store)

	org, err := service.CreateBusiness(owner.Principal(), "Example Business")
	if err != nil {
		t.Fatalf("CreateBusiness() error = %v", err)
	}
	if org.PlanID != "plan_starter" || org.BillingStatus != "billing_required" || org.Members[owner.User.ID].Role != roleOwner {
		t.Fatalf("new organization = %#v, want starter plan, required billing, and owner membership", org)
	}
	ownerAccount, _ := store.Snapshot(owner.ID)
	ownerPrincipal := ownerAccount.Principal()
	if _, err := service.CreateProject(ownerPrincipal, "First Project", "GRU", "GRU", []string{"GRU"}); !errors.Is(err, ErrBillingNeeded) {
		t.Fatalf("CreateProject before billing error = %v, want ErrBillingNeeded", err)
	}
	if _, err := service.ForOrganization(member.Principal(), org.ID); !errors.Is(err, core.ErrForbidden) && !errors.Is(err, core.ErrNotFound) {
		t.Fatalf("non-member organization scope error = %v, want denied", err)
	}
	if err := service.LeaveOrganization(ownerPrincipal); !errors.Is(err, core.ErrConflict) {
		t.Fatalf("owner LeaveOrganization() error = %v, want conflict", err)
	}
	if data := makeOrganizationPage(ownerAccount, org, ownerPrincipal, "", ""); data.CanLeave {
		t.Fatal("organization owner was offered the leave action")
	}

	if err := store.UpdateAs(ownerPrincipal, func(a *core.Account) error {
		current := a.Organizations[org.ID]
		current.BillingStatus = "active"
		a.Organizations[org.ID] = current
		return nil
	}); err != nil {
		t.Fatalf("activate billing: %v", err)
	}
	project, err := service.CreateProject(ownerPrincipal, "First Project", "GRU", "GRU", []string{"GRU", "SFO"})
	if err != nil {
		t.Fatalf("CreateProject() error = %v", err)
	}
	if project.Region != "GRU" || project.BackupRegion != "GRU" || len(project.Regions) != 2 {
		t.Fatalf("created project placement = %#v, want validated GRU/SFO regions", project)
	}
	if err := service.RenameProject(ownerPrincipal, project.ID, "Renamed Project", "SFO", "GRU", []string{"SFO", "GRU"}); err != nil {
		t.Fatalf("RenameProject() error = %v", err)
	}
	if err := service.SwitchProject(ownerPrincipal, project.ID); err != nil {
		t.Fatalf("SwitchProject() error = %v", err)
	}

	invitation, token, err := service.CreateInvitation(ownerPrincipal, member.User.Email, roleMember)
	if err != nil {
		t.Fatalf("CreateInvitation() error = %v", err)
	}
	if invitation.Email != member.User.Email || token == "" {
		t.Fatalf("invitation = %#v, token empty = %v", invitation, token == "")
	}
	digest := sha256.Sum256([]byte(token))
	joined, err := store.AcceptInvitation(member.Principal(), hex.EncodeToString(digest[:]))
	if err != nil {
		t.Fatalf("AcceptInvitation() error = %v", err)
	}
	memberAccount, _ := store.Snapshot(member.ID)
	memberPrincipal := memberAccount.Principal()
	if memberPrincipal.OrganizationID != org.ID || joined.Members[member.User.ID].Role != roleMember {
		t.Fatalf("joined scope/member = %q/%#v, want business organization/member", memberPrincipal.OrganizationID, joined.Members[member.User.ID])
	}
	if data := makeOrganizationPage(memberAccount, joined, memberPrincipal, "", ""); !data.CanLeave {
		t.Fatal("active non-owner business member was not offered the leave action")
	}

	if err := service.UpdateMemberRole(ownerPrincipal, member.User.ID, roleViewer); err != nil {
		t.Fatalf("UpdateMemberRole() error = %v", err)
	}
	updated, _ := store.Snapshot(owner.ID)
	if got := updated.Organizations[org.ID].Members[member.User.ID].Role; got != roleViewer {
		t.Fatalf("updated member role = %q, want viewer", got)
	}
	if err := service.RemoveMember(ownerPrincipal, member.User.ID); err != nil {
		t.Fatalf("RemoveMember() error = %v", err)
	}
	if _, err := store.SnapshotAs(memberPrincipal); !errors.Is(err, core.ErrForbidden) && !errors.Is(err, core.ErrNotFound) {
		t.Fatalf("removed member old scope error = %v, want denied", err)
	}
	memberAccount, _ = store.Snapshot(member.ID)
	if _, ok := memberAccount.Organizations[org.ID]; ok {
		t.Fatal("removed member retained the shared organization in account scope")
	}
	if _, _, err := service.CreateInvitation(ownerPrincipal, member.User.Email, roleViewer); err != nil {
		t.Fatalf("re-invite after removal: %v", err)
	}
}
