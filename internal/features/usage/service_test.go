package usage

import (
	"testing"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/core/memstore"
)

func TestUsageAndSpendingControlsSupportOrganizationWithoutProjects(t *testing.T) {
	store := memstore.New()
	account, err := store.CreateAccount(t.Context(), core.User{
		ID: "usr_projectless", Email: "projectless@example.test", Name: "Projectless", PasswordHash: "hash",
	})
	if err != nil {
		t.Fatal(err)
	}
	if err := store.Update(t.Context(), account.ID, func(current *core.Account) error {
		org := current.Organizations[account.ActiveOrganizationID]
		org.Projects = make(map[string]core.Project)
		current.Organizations[org.ID] = org
		current.ActiveProjectID = ""
		return nil
	}); err != nil {
		t.Fatal(err)
	}

	principal, err := store.CurrentPrincipal(t.Context(), account.ID, account.User.ID)
	if err != nil {
		t.Fatalf("principal for projectless account was not available: %v", err)
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
	if err := service.SetSpendingControls(t.Context(), principal, spendingControls{OrganizationCapCents: &cap}); err != nil {
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
