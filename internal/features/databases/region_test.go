package databases

import (
	"errors"
	"testing"

	"terrascale.tech/dashboard/internal/core"
)

func TestCreateRejectsUnsupportedPlacementWithoutSaving(t *testing.T) {
	service, p := regionService(t)
	for _, regions := range [][]string{{"FRA"}, {"GRU", "FRA"}, {"SFO", "IAD"}} {
		if _, err := service.Create(t.Context(), p, "unsupported", regions...); !errors.Is(err, core.ErrInvalid) {
			t.Fatalf("Create(%v) = %v, want invalid placement", regions, err)
		}
	}
	saved, err := service.deps.Store.SnapshotAs(t.Context(), p)
	if err != nil {
		t.Fatal(err)
	}
	project, err := saved.ProjectFor(p, p.OrganizationID, p.ProjectID)
	if err != nil || len(project.Databases) != 0 {
		t.Fatalf("rejected placement saved databases: %+v, %v", project.Databases, err)
	}
}
