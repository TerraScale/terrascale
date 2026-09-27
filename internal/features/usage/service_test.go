package usage

import (
	"testing"

	"terrascale.tech/dashboard/internal/core"
)

func TestUsageAndSpendingControlsSupportOrganizationWithoutProjects(t *testing.T) {
	store := core.NewStore()
	account, err := store.CreateAccount(core.User{
		ID: "usr_projectless", Email: "projectless@example.test", Name: "Projectless", PasswordHash: "hash",
	})
	if err != nil {
		t.Fatal(err)
	}
	if err := store.Update(account.ID, func(current *core.Account) error {
		org := current.Organizations[account.ActiveOrganizationID]
		org.Projects = make(map[string]core.Project)
		current.Organizations[org.ID] = org
		current.ActiveProjectID = ""
		return nil
	}); err != nil {
		t.Fatal(err)
	}

	principal, ok := store.CurrentPrincipal(account.ID, account.User.ID)
	if !ok {
		t.Fatal("principal for projectless account was not available")
	}
	service := Service{deps: core.Deps{Store: store, Demo: true}}

	report, err := service.Usage(t.Context(), principal, "")
	if err != nil {
		t.Fatalf("Usage without a project: %v", err)
	}
	if report.OrganizationID != account.ActiveOrganizationID || report.ProjectID != "" || !report.CanManage {
		t.Fatalf("Usage without a project = %+v", report)
	}

	cap := int64(1200)
	if err := service.SetSpendingControls(principal, spendingControls{OrganizationCapCents: &cap}); err != nil {
		t.Fatalf("SetSpendingControls without a project: %v", err)
	}
	updated, err := service.Usage(t.Context(), principal, "")
	if err != nil {
		t.Fatalf("Usage after spending-control update: %v", err)
	}
	if updated.OrganizationID != report.OrganizationID || updated.ProjectID != "" || updated.SpendCapCents != cap {
		t.Fatalf("Usage after spending-control update = %+v", updated)
	}
}
