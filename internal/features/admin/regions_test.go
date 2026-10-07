package admin

import (
	"testing"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/core/memstore"
)

func TestEstateListsOnlyGRUAndSFO(t *testing.T) {
	store := memstore.New()
	account, err := store.CreateAccount(t.Context(), core.User{Name: "Admin", Email: "admin@example.test", PasswordHash: "hash", PlatformAdmin: true})
	if err != nil {
		t.Fatal(err)
	}
	p := account.Principal()
	if err := store.UpdateAs(t.Context(), p, func(a *core.Account) error {
		org := a.Organizations[p.OrganizationID]
		project := org.Projects[p.ProjectID]
		project.Region, project.Regions = "XXX", []string{"XXX"}
		org.Projects[project.ID] = project
		a.Organizations[org.ID] = org
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	data, err := NewService(store).page(t.Context(), p, "estate", "")
	if err != nil {
		t.Fatal(err)
	}
	if len(data.Regions) != 2 || data.Regions[0].Region != "GRU" || data.Regions[1].Region != "SFO" {
		t.Fatalf("estate regions = %+v, want only GRU and SFO", data.Regions)
	}
}
