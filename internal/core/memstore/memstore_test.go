package memstore

import (
	"context"
	"errors"
	"testing"

	"terrascale.tech/dashboard/internal/core"
)

// ctx is the context of every test call; the memory store ignores it.
var ctx = context.Background()

// addDatabase adds one database to the principal's selected project.
func addDatabase(store *Store, p core.Principal) error {
	return store.UpdateAs(ctx, p, func(a *core.Account) error {
		org := a.Organizations[p.OrganizationID]
		project := org.Projects[p.ProjectID]
		id := core.NewID("db")
		project.Databases[id] = core.Database{ID: id, ProjectID: project.ID, Name: id}
		org.Projects[project.ID] = project
		a.Organizations[org.ID] = org
		return nil
	})
}

func setPlan(store *Store, p core.Principal, planID string) error {
	return store.UpdateAs(ctx, p, func(a *core.Account) error {
		org := a.Organizations[p.OrganizationID]
		org.PlanID = planID
		a.Organizations[org.ID] = org
		return nil
	})
}

func databaseCount(t *testing.T, store *Store, p core.Principal) int {
	t.Helper()
	a, err := store.SnapshotAs(ctx, p)
	if err != nil {
		t.Fatal(err)
	}
	return a.Organizations[p.OrganizationID].DatabaseCount()
}

func TestUpdateRejectsDatabasesPastThePlanLimit(t *testing.T) {
	store := New()
	account, err := store.CreateAccount(ctx, core.User{Name: "Owner", Email: "limit@example.test", PasswordHash: "unused"})
	if err != nil {
		t.Fatal(err)
	}
	p := account.Principal()

	if err := addDatabase(store, p); err != nil {
		t.Fatalf("first database on Free = %v, want it created", err)
	}
	if err := addDatabase(store, p); !errors.Is(err, core.ErrDatabaseLimit) {
		t.Fatalf("second database on Free = %v, want core.ErrDatabaseLimit", err)
	}
	if got := databaseCount(t, store, p); got != 1 {
		t.Fatalf("databases after a rejected add = %d, want 1", got)
	}
}

func TestUpdateKeepsDatabasesPastTheLimitAfterADowngrade(t *testing.T) {
	store := New()
	account, err := store.CreateAccount(ctx, core.User{Name: "Owner", Email: "downgrade@example.test", PasswordHash: "unused"})
	if err != nil {
		t.Fatal(err)
	}
	p := account.Principal()
	if err := setPlan(store, p, "plan_starter"); err != nil {
		t.Fatal(err)
	}
	for range 3 {
		if err := addDatabase(store, p); err != nil {
			t.Fatalf("database on Starter = %v", err)
		}
	}

	if err := setPlan(store, p, "plan_free"); err != nil {
		t.Fatalf("downgrade with 3 databases = %v, want the plan to change", err)
	}
	if err := store.UpdateAs(ctx, p, func(a *core.Account) error {
		a.User.Phone = "+55 11 5555-0100"
		return nil
	}); err != nil {
		t.Fatalf("unrelated change past the limit = %v, want it saved", err)
	}
	if err := addDatabase(store, p); !errors.Is(err, core.ErrDatabaseLimit) {
		t.Fatalf("database past the limit = %v, want core.ErrDatabaseLimit", err)
	}
	if err := store.UpdateAs(ctx, p, func(a *core.Account) error {
		org := a.Organizations[p.OrganizationID]
		project := org.Projects[p.ProjectID]
		for id := range project.Databases {
			delete(project.Databases, id)
			break
		}
		org.Projects[project.ID] = project
		a.Organizations[org.ID] = org
		return nil
	}); err != nil {
		t.Fatalf("deleting a database past the limit = %v, want it deleted", err)
	}
	if got := databaseCount(t, store, p); got != 2 {
		t.Fatalf("databases = %d, want 2", got)
	}
}

func TestUpdateAccountAsAdminRejectsDatabasesPastThePlanLimit(t *testing.T) {
	store := New()
	admin, err := store.CreateAccount(ctx, core.User{Name: "Admin", Email: "admin@example.test", PasswordHash: "unused", PlatformAdmin: true})
	if err != nil {
		t.Fatal(err)
	}
	account, err := store.CreateAccount(ctx, core.User{Name: "Owner", Email: "target@example.test", PasswordHash: "unused"})
	if err != nil {
		t.Fatal(err)
	}
	p := account.Principal()
	if err := addDatabase(store, p); err != nil {
		t.Fatal(err)
	}
	err = store.UpdateAccountAsAdmin(ctx, admin.Principal(), account.ID, func(a *core.Account) error {
		org := a.Organizations[p.OrganizationID]
		project := org.Projects[p.ProjectID]
		project.Databases["db_admin"] = core.Database{ID: "db_admin", ProjectID: project.ID, Name: "admin"}
		org.Projects[project.ID] = project
		a.Organizations[org.ID] = org
		return nil
	})
	if !errors.Is(err, core.ErrDatabaseLimit) {
		t.Fatalf("admin database past the limit = %v, want core.ErrDatabaseLimit", err)
	}
}
