package core

import (
	"errors"
	"testing"
)

// addDatabase adds one database to the principal's selected project.
func addDatabase(store *Store, p Principal) error {
	return store.UpdateAs(p, func(a *Account) error {
		org := a.Organizations[p.OrganizationID]
		project := org.Projects[p.ProjectID]
		id := NewID("db")
		project.Databases[id] = Database{ID: id, ProjectID: project.ID, Name: id}
		org.Projects[project.ID] = project
		a.Organizations[org.ID] = org
		return nil
	})
}

func setPlan(store *Store, p Principal, planID string) error {
	return store.UpdateAs(p, func(a *Account) error {
		org := a.Organizations[p.OrganizationID]
		org.PlanID = planID
		a.Organizations[org.ID] = org
		return nil
	})
}

func databaseCount(t *testing.T, store *Store, p Principal) int {
	t.Helper()
	a, err := store.SnapshotAs(p)
	if err != nil {
		t.Fatal(err)
	}
	return a.Organizations[p.OrganizationID].DatabaseCount()
}

func TestUpdateRejectsDatabasesPastThePlanLimit(t *testing.T) {
	store := NewStore()
	account, err := store.CreateAccount(User{Name: "Owner", Email: "limit@example.test", PasswordHash: "unused"})
	if err != nil {
		t.Fatal(err)
	}
	p := account.Principal()

	if err := addDatabase(store, p); err != nil {
		t.Fatalf("first database on Free = %v, want it created", err)
	}
	if err := addDatabase(store, p); !errors.Is(err, ErrDatabaseLimit) {
		t.Fatalf("second database on Free = %v, want ErrDatabaseLimit", err)
	}
	if got := databaseCount(t, store, p); got != 1 {
		t.Fatalf("databases after a rejected add = %d, want 1", got)
	}
}

func TestUpdateKeepsDatabasesPastTheLimitAfterADowngrade(t *testing.T) {
	store := NewStore()
	account, err := store.CreateAccount(User{Name: "Owner", Email: "downgrade@example.test", PasswordHash: "unused"})
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
	if err := store.UpdateAs(p, func(a *Account) error {
		a.User.Phone = "+55 11 5555-0100"
		return nil
	}); err != nil {
		t.Fatalf("unrelated change past the limit = %v, want it saved", err)
	}
	if err := addDatabase(store, p); !errors.Is(err, ErrDatabaseLimit) {
		t.Fatalf("database past the limit = %v, want ErrDatabaseLimit", err)
	}
	if err := store.UpdateAs(p, func(a *Account) error {
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
	store := NewStore()
	admin, err := store.CreateAccount(User{Name: "Admin", Email: "admin@example.test", PasswordHash: "unused", PlatformAdmin: true})
	if err != nil {
		t.Fatal(err)
	}
	account, err := store.CreateAccount(User{Name: "Owner", Email: "target@example.test", PasswordHash: "unused"})
	if err != nil {
		t.Fatal(err)
	}
	p := account.Principal()
	if err := addDatabase(store, p); err != nil {
		t.Fatal(err)
	}
	err = store.UpdateAccountAsAdmin(admin.Principal(), account.ID, func(a *Account) error {
		org := a.Organizations[p.OrganizationID]
		project := org.Projects[p.ProjectID]
		project.Databases["db_admin"] = Database{ID: "db_admin", ProjectID: project.ID, Name: "admin"}
		org.Projects[project.ID] = project
		a.Organizations[org.ID] = org
		return nil
	})
	if !errors.Is(err, ErrDatabaseLimit) {
		t.Fatalf("admin database past the limit = %v, want ErrDatabaseLimit", err)
	}
}

func TestValidDatabaseName(t *testing.T) {
	for name, want := range map[string]bool{
		"orders": true, "a": true, "core-primary-2": true, "0-9": true,
		"": false, "-orders": false, "orders-": false, "Orders": false, "orders_v2": false,
		"core primary": false, "a/b": false, "Restored_UPPER!": false,
		"abcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvwxyzabcdefghijk":  true,
		"abcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvwxyzabcdefghijkl": false,
	} {
		if got := ValidDatabaseName(name); got != want {
			t.Errorf("ValidDatabaseName(%q) = %v, want %v", name, got, want)
		}
	}
}
