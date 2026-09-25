package core

import (
	"errors"
	"testing"
	"time"
)

func TestPlatformAdminActionLogRecordsPersistedActorAndReturnsSnapshot(t *testing.T) {
	store := NewStore()
	account, err := store.CreateAccount(User{
		Email: "admin@example.test", Name: "Admin", PasswordHash: "test", PlatformAdmin: true,
	})
	if err != nil {
		t.Fatal(err)
	}
	principal := account.Principal()
	principal.PlatformAdmin = false

	before := time.Now().UTC()
	if err := store.RecordPlatformAdminAction(principal, "pause", "db-123", "Investigation requested"); err != nil {
		t.Fatalf("record action: %v", err)
	}
	after := time.Now().UTC()

	entries, err := store.PlatformAdminActions(principal)
	if err != nil {
		t.Fatalf("read action log: %v", err)
	}
	if len(entries) != 1 {
		t.Fatalf("action log length = %d, want 1", len(entries))
	}
	entry := entries[0]
	if entry.ActorUserID != account.User.ID || entry.Action != "pause" || entry.Target != "db-123" || entry.Reason != "Investigation requested" {
		t.Fatalf("recorded action = %+v", entry)
	}
	if entry.RecordedAt.Before(before) || entry.RecordedAt.After(after) || entry.RecordedAt.Location() != time.UTC {
		t.Fatalf("recorded time = %s, want UTC time between %s and %s", entry.RecordedAt, before, after)
	}

	entries[0].Reason = "mutated caller snapshot"
	entries, err = store.PlatformAdminActions(principal)
	if err != nil {
		t.Fatalf("read action log after snapshot mutation: %v", err)
	}
	if entries[0].Reason != "Investigation requested" {
		t.Fatalf("store log changed through returned snapshot: %+v", entries[0])
	}
}

func TestPlatformAdminActionLogRejectsForgedAndInvalidCalls(t *testing.T) {
	store := NewStore()
	admin, err := store.CreateAccount(User{
		Email: "admin@example.test", Name: "Admin", PasswordHash: "test", PlatformAdmin: true,
	})
	if err != nil {
		t.Fatal(err)
	}
	member, err := store.CreateAccount(User{
		Email: "member@example.test", Name: "Member", PasswordHash: "test",
	})
	if err != nil {
		t.Fatal(err)
	}

	forged := member.Principal()
	forged.PlatformAdmin = true
	if err := store.RecordPlatformAdminAction(forged, "pause", "db-123", "Investigation requested"); !errors.Is(err, ErrForbidden) {
		t.Fatalf("record with forged principal error = %v, want forbidden", err)
	}

	for _, input := range [][3]string{{" ", "db-123", "reason"}, {"pause", " ", "reason"}, {"pause", "db-123", "\t"}} {
		if err := store.RecordPlatformAdminAction(admin.Principal(), input[0], input[1], input[2]); !errors.Is(err, ErrInvalid) {
			t.Errorf("record with input %q error = %v, want invalid", input, err)
		}
	}

	entries, err := store.PlatformAdminActions(admin.Principal())
	if err != nil {
		t.Fatalf("read action log: %v", err)
	}
	if len(entries) != 0 {
		t.Fatalf("invalid or forbidden calls appended records: %+v", entries)
	}
	if _, err := store.PlatformAdminActions(forged); !errors.Is(err, ErrForbidden) {
		t.Fatalf("read with forged principal error = %v, want forbidden", err)
	}
}
