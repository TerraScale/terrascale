package app

import (
	"context"
	"fmt"
	"net/http"
	"net/http/httptest"
	"testing"
	"time"

	"terrascale.tech/dashboard/internal/adapters/terrabase"
	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/core/memstore"
	"terrascale.tech/dashboard/internal/features/auth"
	"terrascale.tech/dashboard/internal/platform/session"
	"terrascale.tech/dashboard/internal/platform/terrabasetest"
)

// TestSessionsKeepTheAccessTokenInTerraBase checks, with the TerraBase
// session store production uses, that a ts-auth session saved before
// sessions kept the access token renews into one on its first TerraBase
// call, and that another machine reads the renewed token from TerraBase
// without rechecking again.
func TestSessionsKeepTheAccessTokenInTerraBase(t *testing.T) {
	if testing.Short() {
		t.Skip("starts pinned TerraBase release")
	}
	tb := terrabasetest.Start(t)
	db, _ := terrabasetest.Database(t, tb, "dashboard")
	if err := terrabase.EnsureCollections(t.Context(), db); err != nil {
		t.Fatal(err)
	}
	accounts := memstore.New()
	account, err := accounts.CreateAccount(t.Context(), core.User{Name: "Token", Email: "token@example.test", PasswordHash: "unused"})
	if err != nil {
		t.Fatal(err)
	}
	expiry := time.Now().Add(time.Hour).UTC().Format(time.RFC3339)
	refreshes := 0
	options := session.Options{Store: terrabase.NewSessions(db), UserToken: auth.AccessToken,
		Refresh: func(context.Context, string) (string, time.Time, error) {
			refreshes++
			return fmt.Sprintf(`{"sub":"s","token":"r2","access":"access-2","accessExpiry":%q}`, expiry), time.Now().Add(time.Hour), nil
		}}
	start := httptest.NewRecorder()
	if err := session.New(accounts, options).StartRefreshable(start, httptest.NewRequest(http.MethodGet, "/", nil),
		account.Principal(), `{"sub":"s","token":"r1"}`, time.Now().Add(time.Hour)); err != nil {
		t.Fatal(err)
	}
	request := httptest.NewRequest(http.MethodGet, "/", nil)
	for _, cookie := range start.Result().Cookies() {
		request.AddCookie(cookie)
	}
	if token := session.New(accounts, options).UserToken(request); token != "access-2" || refreshes != 1 {
		t.Fatalf("first lookup = %q after %d rechecks, want access-2 from one", token, refreshes)
	}
	if token := session.New(accounts, options).UserToken(request); token != "access-2" || refreshes != 1 {
		t.Fatalf("another machine's lookup = %q after %d rechecks, want the token saved in TerraBase", token, refreshes)
	}
}
