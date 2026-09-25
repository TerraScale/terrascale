package session

import (
	"crypto/sha256"
	"errors"
	"net/http"
	"net/http/httptest"
	"testing"
	"time"

	"terrascale.tech/dashboard/internal/core"
)

func TestExpiredSessionsArePruned(t *testing.T) {
	store := core.NewStore()
	a, err := store.CreateAccount(core.User{Name: "A", Email: "a@example.com", PasswordHash: "hashed"})
	if err != nil {
		t.Fatal(err)
	}
	m := New(store, Options{})
	expired := sha256.Sum256([]byte("expired"))
	m.items[expired] = entry{accountID: a.ID, userID: a.User.ID, expiresAt: time.Now().Add(-time.Second)}
	r := httptest.NewRequest(http.MethodGet, "http://dashboard.test/", nil)
	r.AddCookie(&http.Cookie{Name: m.opts.CookieName, Value: "expired"})
	if _, ok := m.Principal(r); ok {
		t.Fatal("expired session accepted")
	}
	if _, ok := m.items[expired]; ok {
		t.Fatal("expired session was retained after read")
	}
	m.items[expired] = entry{accountID: a.ID, userID: a.User.ID, expiresAt: time.Now().Add(-time.Second)}
	if err := m.Start(httptest.NewRecorder(), httptest.NewRequest(http.MethodPost, "http://dashboard.test/login", nil), a.Principal()); err != nil {
		t.Fatal(err)
	}
	if _, ok := m.items[expired]; ok {
		t.Fatal("expired session was retained after new login")
	}
}

func TestPasswordVersionInvalidatesSessionAndStaleLogin(t *testing.T) {
	store := core.NewStore()
	a, err := store.CreateAccount(core.User{Name: "A", Email: "a@example.com", PasswordHash: "old-hash"})
	if err != nil {
		t.Fatal(err)
	}
	m := New(store, Options{})
	r := httptest.NewRequest(http.MethodPost, "http://dashboard.test/login", nil)
	w := httptest.NewRecorder()
	if err := m.StartIfPasswordHash(w, r, a.Principal(), "old-hash"); err != nil {
		t.Fatal(err)
	}
	cookie := w.Result().Cookies()[0]
	if err := store.UpdateAs(a.Principal(), func(a *core.Account) error {
		a.User.PasswordHash = "new-hash"
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	oldRequest := httptest.NewRequest(http.MethodGet, "http://dashboard.test/", nil)
	oldRequest.AddCookie(cookie)
	if _, valid := m.Principal(oldRequest); valid {
		t.Fatal("pre-change session remained valid")
	}
	if err := m.StartIfPasswordHash(httptest.NewRecorder(), r, a.Principal(), "old-hash"); !errors.Is(err, core.ErrUnauthorized) {
		t.Fatalf("stale principal login: %v", err)
	}
	current, _ := store.Snapshot(a.ID)
	if err := m.StartIfPasswordHash(httptest.NewRecorder(), r, current.Principal(), "old-hash"); !errors.Is(err, core.ErrConflict) {
		t.Fatalf("stale password login: %v", err)
	}
	if err := m.StartIfPasswordHash(httptest.NewRecorder(), r, current.Principal(), "new-hash"); err != nil {
		t.Fatalf("current password login: %v", err)
	}
}
