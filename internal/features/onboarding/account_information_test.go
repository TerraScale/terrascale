package onboarding

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

// An invited user who joins another organization before finishing onboarding
// has that organization selected when they save their name.
func TestAccountInformationRenamesAMemberOfAnotherOrganization(t *testing.T) {
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
	if _, err := store.AcceptInvitation(member.Principal(), token); err != nil {
		t.Fatal(err)
	}
	principal, ok := store.CurrentPrincipal(member.ID, member.User.ID)
	if !ok || principal.OrganizationID != ownerPrincipal.OrganizationID {
		t.Fatalf("member principal = %+v, want the owner's organization selected", principal)
	}

	sessions := session.New(store, session.Options{})
	mux := http.NewServeMux()
	Mount(mux, core.Deps{Store: store, Sessions: sessions, Demo: true})
	signIn := httptest.NewRecorder()
	if err := sessions.Start(signIn, httptest.NewRequest(http.MethodGet, "/", nil), principal); err != nil {
		t.Fatal(err)
	}
	form := url.Values{"step": {"account-information"}, "displayName": {"New Name"}}
	request := httptest.NewRequest(http.MethodPost, "/onboarding/step", strings.NewReader(form.Encode()))
	request.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	for _, cookie := range signIn.Result().Cookies() {
		request.AddCookie(cookie)
	}
	response := httptest.NewRecorder()
	mux.ServeHTTP(response, request)
	if response.Code != http.StatusSeeOther || response.Header().Get("Location") != "/onboarding" {
		t.Fatalf("account information = %d %q %s, want 303 to /onboarding", response.Code, response.Header().Get("Location"), response.Body)
	}

	updated, err := store.SnapshotAs(principal)
	if err != nil {
		t.Fatal(err)
	}
	for id, org := range updated.Organizations {
		if got := org.Members[member.User.ID].Name; got != "New Name" {
			t.Errorf("member name in %s organization %s = %q, want New Name", org.Kind, id, got)
		}
	}
}
