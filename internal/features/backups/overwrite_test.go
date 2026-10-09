package backups

import (
	"errors"
	"testing"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/core/memstore"
)

func TestOverwriteRestoresTheBackupAfterTheNameIsConfirmed(t *testing.T) {
	store := memstore.New()
	account, err := store.CreateAccount(t.Context(), core.User{Name: "Owner", Email: "overwrite-owner@example.test", PasswordHash: "unused"})
	if err != nil {
		t.Fatal(err)
	}
	p := account.Principal()
	setDocument := func(body string) {
		t.Helper()
		if err := store.UpdateAs(t.Context(), p, func(a *core.Account) error {
			org := a.Organizations[p.OrganizationID]
			project := org.Projects[p.ProjectID]
			db := project.Databases["16KDJ28DR0XG7QGWEZ4VJ9Z1VX"]
			db.ID, db.ProjectID, db.Name, db.Status = "16KDJ28DR0XG7QGWEZ4VJ9Z1VX", project.ID, "orders", "ready"
			db.Documents = map[string]map[string][]byte{"orders": {"o1": []byte(body)}}
			project.Databases[db.ID] = db
			org.Projects[project.ID] = project
			a.Organizations[org.ID] = org
			return nil
		}); err != nil {
			t.Fatal(err)
		}
	}
	document := func() string {
		t.Helper()
		snapshot, err := store.SnapshotAs(t.Context(), p)
		if err != nil {
			t.Fatal(err)
		}
		return string(snapshot.Organizations[p.OrganizationID].Projects[p.ProjectID].Databases["16KDJ28DR0XG7QGWEZ4VJ9Z1VX"].Documents["orders"]["o1"])
	}

	service := NewService(core.Deps{Store: store, Demo: true})
	setDocument(`{"total":1}`)
	backup, err := service.Create(t.Context(), p, "16KDJ28DR0XG7QGWEZ4VJ9Z1VX")
	if err != nil {
		t.Fatal(err)
	}
	setDocument(`{"total":2}`)

	if _, err := service.Overwrite(t.Context(), p, "16KDJ28DR0XG7QGWEZ4VJ9Z1VX", backup.ID, "order"); !errors.Is(err, errConfirmName) {
		t.Fatalf("overwrite with the wrong name = %v, want errConfirmName", err)
	}
	if got := document(); got != `{"total":2}` {
		t.Fatalf("after a refused overwrite the document is %s, want it unchanged", got)
	}
	if _, err := service.Overwrite(t.Context(), p, "16KDJ28DR0XG7QGWEZ4VJ9Z1VX", "0CKNEYH9FDDFD7NWQ6NQRGQA91", "orders"); !errors.Is(err, errBackupMissing) {
		t.Fatalf("overwrite from a missing backup = %v, want errBackupMissing", err)
	}
	if _, err := service.Overwrite(t.Context(), p, "16KDJ28DR0XG7QGWEZ4VJ9Z1VX", backup.ID, "orders"); err != nil {
		t.Fatal(err)
	}
	if got := document(); got != `{"total":1}` {
		t.Fatalf("after the overwrite the document is %s, want the backup's", got)
	}
	snapshot, err := store.SnapshotAs(t.Context(), p)
	if err != nil {
		t.Fatal(err)
	}
	events := snapshot.AuditEvents
	if len(events) == 0 || events[len(events)-1].Action != "backup.overwritten" {
		t.Fatalf("audit events = %+v, want backup.overwritten last", events)
	}
}
