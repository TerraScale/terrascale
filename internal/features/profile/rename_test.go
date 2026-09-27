package profile

import (
	"crypto/sha256"
	"encoding/hex"
	"net/http"
	"net/http/httptest"
	"net/url"
	"strings"
	"testing"
	"time"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/platform/session"
)

func TestProfileRenameReachesTheUsersOtherOrganizations(t *testing.T) {
	store := core.NewStore()
	owner, err := store.CreateAccount(core.User{Name: "Owner", Email: "owner@example.test", PasswordHash: "unused"})
	if err != nil {
		t.Fatal(err)
	}
	ownerPrincipal := owner.Principal()
	digest := sha256.Sum256([]byte("invitation-token"))
	token := hex.EncodeToString(digest[:])
	if err := store.UpdateAs(ownerPrincipal, func(a *core.Account) error {
		org := a.Organizations[ownerPrincipal.OrganizationID]
		org.Invitations["inv_member"] = core.Invitation{
			ID: "inv_member", Email: "member@example.test", Role: "member", Status: "pending",
			TokenHash: token, ExpiresAt: time.Now().Add(time.Hour),
		}
		a.Organizations[org.ID] = org
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	member, err := store.CreateAccount(core.User{Name: "Old Name", Email: "member@example.test", PasswordHash: "unused"})
	if err != nil {
		t.Fatal(err)
	}
	personalID, personalProjectID := member.ActiveOrganizationID, member.ActiveProjectID
	if _, err := store.AcceptInvitation(member.Principal(), token); err != nil {
		t.Fatal(err)
	}
	// The member works in their Personal organization while also belonging
	// to the owner's.
	if err := store.Update(member.ID, func(a *core.Account) error {
		a.ActiveOrganizationID, a.ActiveProjectID = personalID, personalProjectID
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	principal, ok := store.CurrentPrincipal(member.ID, member.User.ID)
	if !ok {
		t.Fatal("member has no principal")
	}

	sessions := session.New(store, session.Options{})
	mux := http.NewServeMux()
	Mount(mux, core.Deps{Store: store, Sessions: sessions, Demo: true})
	signIn := httptest.NewRecorder()
	if err := sessions.Start(signIn, httptest.NewRequest(http.MethodGet, "/", nil), principal); err != nil {
		t.Fatal(err)
	}
	form := url.Values{"name": {"New Name"}, "phoneNumber": {""}}
	request := httptest.NewRequest(http.MethodPost, "/api/v1/dashboard/profile", strings.NewReader(form.Encode()))
	request.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	for _, cookie := range signIn.Result().Cookies() {
		request.AddCookie(cookie)
	}
	response := httptest.NewRecorder()
	mux.ServeHTTP(response, request)
	if response.Code != http.StatusSeeOther || response.Header().Get("Location") != "/profile" {
		t.Fatalf("rename = %d %s, want 303 to /profile", response.Code, response.Header().Get("Location"))
	}

	a, err := store.SnapshotAs(ownerPrincipal)
	if err != nil {
		t.Fatal(err)
	}
	if got := a.Organizations[ownerPrincipal.OrganizationID].Members[member.User.ID].Name; got != "New Name" {
		t.Errorf("member name in the owner's organization = %q, want New Name", got)
	}
}
