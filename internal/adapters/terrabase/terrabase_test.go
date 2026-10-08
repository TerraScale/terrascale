package terrabase

import (
	"errors"
	"fmt"
	"strings"
	"testing"
	"time"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/core/storetest"
	"terrascale.tech/dashboard/internal/platform/session"
	"terrascale.tech/dashboard/internal/platform/terrabasetest"
	"terrascale.tech/dashboard/internal/platform/throttle"
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

// TestMirrorOrganizationsFollowsEveryServiceDatabase covers what a platform
// admin's sign-in mirrors: every TerraBase organization the admin belongs
// to, with every project and database, including ones a later service adds.
func TestMirrorOrganizationsFollowsEveryServiceDatabase(t *testing.T) {
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
	admin, err := store.CreateAccount(t.Context(), core.User{Name: "Admin", Email: "admin@example.test", PasswordHash: "unused", PlatformAdmin: true})
	if err != nil {
		t.Fatal(err)
	}
	m, err := v1.NewManagement(v1.Config{BaseURL: tb.URL, HTTPClient: tb.Client})
	if err != nil {
		t.Fatal(err)
	}
	user := m.WithToken(tb.Token(t, "user-admin", "admin@example.test"))
	addService := func(org, name string) {
		t.Helper()
		project, err := user.CreateProject(t.Context(), org, name)
		if err != nil {
			t.Fatal(err)
		}
		if _, err := user.CreateDatabase(t.Context(), org, project.ID, strings.TrimPrefix(name, "ts-")); err != nil {
			t.Fatal(err)
		}
	}
	terrascale, err := user.CreateOrganization(t.Context(), "terrascale")
	if err != nil {
		t.Fatal(err)
	}
	other, err := user.CreateOrganization(t.Context(), "other")
	if err != nil {
		t.Fatal(err)
	}
	addService(terrascale.ID, "ts-auth")
	addService(terrascale.ID, "ts-dashboard")
	addService(other.ID, "ts-status")
	if err := MirrorOrganizations(t.Context(), store, user, "missing@example.test"); !errors.Is(err, core.ErrNotFound) {
		t.Fatalf("mirror without a dashboard account = %v, want ErrNotFound", err)
	}
	if _, err := store.FindByEmail(t.Context(), "missing@example.test"); !errors.Is(err, core.ErrNotFound) {
		t.Fatalf("mirror created an identity account: %v", err)
	}
	mirrored := func() (databases map[string]bool) {
		t.Helper()
		if err := MirrorOrganizations(t.Context(), store, user, "admin@example.test"); err != nil {
			t.Fatal(err)
		}
		account, err := store.FindByEmail(t.Context(), "admin@example.test")
		if err != nil || account.ID != admin.ID {
			t.Fatalf("admin account = %v, %v", account.ID, err)
		}
		databases = map[string]bool{}
		for _, org := range account.Organizations {
			for _, project := range org.Projects {
				for _, database := range project.Databases {
					if !core.SystemDatabase(database) {
						t.Errorf("mirrored database %s has a dashboard id", database.ID)
					}
					databases[org.Name+"/"+project.Name+"/"+database.Name] = true
				}
			}
		}
		return databases
	}
	if got := mirrored(); len(got) != 3 || !got["terrascale/ts-auth/auth"] || !got["terrascale/ts-dashboard/dashboard"] || !got["other/ts-status/status"] {
		t.Fatalf("mirrored %v", got)
	}
	addService(terrascale.ID, "ts-router")
	if got := mirrored(); len(got) != 4 || !got["terrascale/ts-router/router"] {
		t.Fatalf("after ts-router joined, mirrored %v", got)
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

// TestThrottleAllowsAKeyItHasNeverSeen reserves a first attempt for a new
// key, which a zero Due once turned into a refusal for centuries.
func TestThrottleAllowsAKeyItHasNeverSeen(t *testing.T) {
	if testing.Short() {
		t.Skip("starts pinned TerraBase release")
	}
	tb := terrabasetest.Start(t)
	db, _ := terrabasetest.Database(t, tb, "throttle")
	if err := EnsureCollections(t.Context(), db); err != nil {
		t.Fatal(err)
	}
	store, now := NewThrottle(db), time.Now()
	bucket := throttle.Bucket{Key: "client 203.0.113.9", Budget: throttle.Budget{Burst: 2, Interval: time.Minute}}
	for attempt, want := range []bool{true, true, false} {
		wait, allowed, err := store.Reserve(t.Context(), now, []throttle.Bucket{bucket})
		if err != nil || allowed != want || wait < 0 || wait > time.Minute {
			t.Fatalf("attempt %d = %v %v %v, want allowed=%v with a wait within the interval", attempt+1, wait, allowed, err, want)
		}
	}
}
