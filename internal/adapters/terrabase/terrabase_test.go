package terrabase

import (
	"errors"
	"fmt"
	"testing"
	"time"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/core/storetest"
	"terrascale.tech/dashboard/internal/platform/session"
	"terrascale.tech/dashboard/internal/platform/terrabasetest"
	v1 "terrascale.tech/sdk/v1"
)

func TestConformance(t *testing.T) {
	if testing.Short() {
		t.Skip("starts pinned TerraBase release")
	}
	tb := terrabasetest.Start(t)
	n := 0
	storetest.Run(t, func(t *testing.T) core.Store {
		n++
		db, _ := terrabasetest.Database(t, tb, fmt.Sprintf("contract-%02d", n))
		if err := EnsureCollections(t.Context(), db); err != nil {
			t.Fatal(err)
		}
		store := New(db)
		if err := store.SeedPlans(t.Context()); err != nil {
			t.Fatal(err)
		}
		return store
	})
}

// The dashboard is stateless: a restarted process with a fresh client reads
// everything back from TerraBase.
func TestPersistsAcrossDashboardRestart(t *testing.T) {
	if testing.Short() {
		t.Skip("starts pinned TerraBase release")
	}
	tb := terrabasetest.Start(t)
	db, key := terrabasetest.Database(t, tb, "restart")
	if err := EnsureCollections(t.Context(), db); err != nil {
		t.Fatal(err)
	}
	store := New(db)
	if err := store.SeedPlans(t.Context()); err != nil {
		t.Fatal(err)
	}
	want, err := store.CreateAccount(t.Context(), core.User{
		Name:         "Restarted",
		Email:        "restart@example.test",
		PasswordHash: "hash",
	})
	if err != nil {
		t.Fatal(err)
	}

	client, err := v1.New(v1.Config{BaseURL: tb.URL, Token: key, HTTPClient: tb.Client})
	if err != nil {
		t.Fatal(err)
	}
	restarted := New(client.Database(db.Project(), db.Name()))
	got, err := restarted.FindByEmail(t.Context(), want.User.Email)
	if err != nil {
		t.Fatal(err)
	}
	if got.ID != want.ID || got.User.ID != want.User.ID {
		t.Fatalf("restarted account = %+v, want %+v", got, want)
	}
}

func TestImportOrganizationIsIdempotent(t *testing.T) {
	if testing.Short() {
		t.Skip("starts pinned TerraBase release")
	}
	tb := terrabasetest.Start(t)
	db, _ := terrabasetest.Database(t, tb, "dashboard")
	if err := EnsureCollections(t.Context(), db); err != nil {
		t.Fatal(err)
	}
	store := New(db)
	if err := store.SeedPlans(t.Context()); err != nil {
		t.Fatal(err)
	}
	m, err := v1.NewManagement(v1.Config{BaseURL: tb.URL, Token: tb.Credential, HTTPClient: tb.Client})
	if err != nil {
		t.Fatal(err)
	}
	operator := m.As("ts-orchestrator")
	org, err := operator.CreateOrganization(t.Context(), "terrascale")
	if err != nil {
		t.Fatal(err)
	}
	for _, name := range []string{"ts-auth", "ts-dashboard"} {
		project, err := operator.CreateProject(t.Context(), org.ID, name)
		if err != nil {
			t.Fatal(err)
		}
		if _, err := operator.CreateDatabase(t.Context(), org.ID, project.ID, "main"); err != nil {
			t.Fatal(err)
		}
	}
	for range 2 {
		imported, err := ImportOrganization(t.Context(), store, operator, "terrascale", "Owner@Example.test")
		if err != nil {
			t.Fatal(err)
		}
		if imported.ID != org.ID || len(imported.Projects) != 2 || imported.DatabaseCount() != 2 {
			t.Fatalf("imported = %s, %d projects, %d databases", imported.ID, len(imported.Projects), imported.DatabaseCount())
		}
	}
	owner, err := store.FindByEmail(t.Context(), "owner@example.test")
	if err != nil {
		t.Fatal(err)
	}
	if owner.User.PasswordHash != OIDCPendingPassword || owner.Organizations[org.ID].Members[owner.User.ID].Role != "owner" {
		t.Fatalf("owner = %+v", owner.User)
	}
	if _, err := ImportOrganization(t.Context(), store, operator, "missing", "owner@example.test"); err == nil {
		t.Fatal("unknown organization imported")
	}
}

// Sessions keep their ts-auth refresh state, and Replace changes or ends one
// only while it still holds the record the caller read.
func TestSessionsReplaceOnlyUnchanged(t *testing.T) {
	if testing.Short() {
		t.Skip("starts pinned TerraBase release")
	}
	tb := terrabasetest.Start(t)
	db, _ := terrabasetest.Database(t, tb, "sessions")
	if err := EnsureCollections(t.Context(), db); err != nil {
		t.Fatal(err)
	}
	sessions := NewSessions(db)
	var key [32]byte
	key[0] = 1
	now := time.Now()
	first := session.Record{
		AccountID: "acct_1", UserID: "usr_1", AuthVersion: 2, ExpiresAt: now.Add(time.Hour),
		Refresh: `{"sub":"sub-1","token":"r1"}`, RefreshAt: now.Add(time.Minute),
	}
	if err := sessions.Put(t.Context(), key, first); err != nil {
		t.Fatal(err)
	}
	stored, err := sessions.Get(t.Context(), key, time.Now())
	if err != nil || stored.Refresh != first.Refresh || !stored.RefreshAt.Equal(first.RefreshAt) {
		t.Fatalf("stored = %+v, %v; want the refresh state kept", stored, err)
	}
	next := stored
	next.Refresh, next.RefreshAt = `{"sub":"sub-1","token":"r2"}`, now.Add(time.Hour)
	changed := stored
	changed.Refresh = `{"sub":"sub-1","token":"other"}`
	if err := sessions.Replace(t.Context(), key, changed, next); !errors.Is(err, session.ErrNotFound) {
		t.Fatalf("Replace of another record = %v, want session.ErrNotFound", err)
	}
	if err := sessions.Replace(t.Context(), key, stored, next); err != nil {
		t.Fatalf("Replace of the unchanged session = %v", err)
	}
	if err := sessions.Replace(t.Context(), key, stored, stored); !errors.Is(err, session.ErrNotFound) {
		t.Fatalf("Replace from a stale read = %v, want session.ErrNotFound", err)
	}
	current, err := sessions.Get(t.Context(), key, time.Now())
	if err != nil || current.Refresh != next.Refresh {
		t.Fatalf("current = %+v, %v; want r2", current, err)
	}
	ended := current
	ended.ExpiresAt = time.Time{}
	if err := sessions.Replace(t.Context(), key, current, ended); err != nil {
		t.Fatalf("ending the session = %v", err)
	}
	if _, err := sessions.Get(t.Context(), key, time.Now()); !errors.Is(err, session.ErrNotFound) {
		t.Fatalf("ended session lookup = %v, want session.ErrNotFound", err)
	}
	if err := sessions.Replace(t.Context(), key, current, next); !errors.Is(err, session.ErrNotFound) {
		t.Fatalf("Replace of an ended session = %v, want session.ErrNotFound", err)
	}
}
