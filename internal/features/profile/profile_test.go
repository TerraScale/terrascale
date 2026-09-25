package profile

import (
	"net/http"
	"net/http/httptest"
	"net/url"
	"strings"
	"testing"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/features/auth"
	"terrascale.tech/dashboard/internal/platform/csrf"
	"terrascale.tech/dashboard/internal/platform/render"
	"terrascale.tech/dashboard/internal/platform/session"
)

const profileTestPassword = "profile-password-123"

type profileTestRig struct {
	store         *core.Store
	account       core.Account
	sessions      *session.Manager
	handler       http.Handler
	csrfToken     string
	csrfCookie    *http.Cookie
	sessionCookie *http.Cookie
}

func newProfileTestRig(t *testing.T) profileTestRig {
	t.Helper()
	hash, err := auth.HashPassword(profileTestPassword)
	if err != nil {
		t.Fatal(err)
	}
	store := core.NewStore()
	account, err := store.CreateAccount(core.User{Name: "Profile Owner", Email: "profile@example.test", PasswordHash: hash})
	if err != nil {
		t.Fatal(err)
	}
	sessions := session.New(store, session.Options{})
	guard := csrf.New(sessions, csrf.Options{})
	mux := http.NewServeMux()
	Mount(mux, core.Deps{Store: store, Sessions: sessions, CSRF: guard, Render: render.New(store, guard), Demo: true})
	request := httptest.NewRequest(http.MethodGet, "http://dashboard.test/profile", nil)
	csrfResponse := httptest.NewRecorder()
	token := guard.Token(csrfResponse, request)
	var csrfCookie *http.Cookie
	for _, cookie := range csrfResponse.Result().Cookies() {
		if cookie.Name == "ts_dashboard_csrf" {
			csrfCookie = cookie
		}
	}
	if csrfCookie == nil {
		t.Fatal("CSRF cookie missing")
	}
	sessionResponse := httptest.NewRecorder()
	if err := sessions.Start(sessionResponse, request, account.Principal()); err != nil {
		t.Fatal(err)
	}
	var sessionCookie *http.Cookie
	for _, cookie := range sessionResponse.Result().Cookies() {
		if cookie.Name == "ts_dashboard_session" {
			sessionCookie = cookie
		}
	}
	if sessionCookie == nil {
		t.Fatal("session cookie missing")
	}
	return profileTestRig{
		store: store, account: account, sessions: sessions, handler: guard.Protect(mux),
		csrfToken: token, csrfCookie: csrfCookie, sessionCookie: sessionCookie,
	}
}

func (rig profileTestRig) post(path string, values url.Values) *httptest.ResponseRecorder {
	values.Set("_csrf", rig.csrfToken)
	request := httptest.NewRequest(http.MethodPost, "http://dashboard.test"+path, strings.NewReader(values.Encode()))
	request.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	request.Header.Set("Origin", "http://dashboard.test")
	request.AddCookie(rig.csrfCookie)
	request.AddCookie(rig.sessionCookie)
	response := httptest.NewRecorder()
	rig.handler.ServeHTTP(response, request)
	return response
}

func (rig profileTestRig) get(path string) *httptest.ResponseRecorder {
	request := httptest.NewRequest(http.MethodGet, "http://dashboard.test"+path, nil)
	request.AddCookie(rig.sessionCookie)
	response := httptest.NewRecorder()
	rig.handler.ServeHTTP(response, request)
	return response
}

func TestEmailChangeUpdatesLookupAndMembershipAtomically(t *testing.T) {
	rig := newProfileTestRig(t)
	otherHash, err := auth.HashPassword("other-password-123")
	if err != nil {
		t.Fatal(err)
	}
	if _, err := rig.store.CreateAccount(core.User{Name: "Other", Email: "taken@example.test", PasswordHash: otherHash}); err != nil {
		t.Fatal(err)
	}
	path := "/api/v1/dashboard/profile/email-change"
	for _, input := range []struct {
		email, password string
	}{
		{"taken@example.test", profileTestPassword},
		{"new@example.test", "wrong-password"},
		{"invalid address", profileTestPassword},
	} {
		response := rig.post(path, url.Values{"newEmail": {input.email}, "password": {input.password}})
		if response.Code != http.StatusSeeOther || !strings.Contains(response.Header().Get("Location"), "error=") {
			t.Fatalf("rejected email change returned %d at %q", response.Code, response.Header().Get("Location"))
		}
		if _, ok := rig.store.FindByEmail("profile@example.test"); !ok {
			t.Fatal("rejected change removed original lookup")
		}
	}
	response := rig.post(path, url.Values{"newEmail": {" NEW@Example.Test "}, "password": {profileTestPassword}})
	if response.Code != http.StatusSeeOther || !strings.Contains(response.Header().Get("Location"), "status=email-changed") {
		t.Fatalf("valid email change returned %d at %q", response.Code, response.Header().Get("Location"))
	}
	if _, oldExists := rig.store.FindByEmail("profile@example.test"); oldExists {
		t.Fatal("old email still resolves")
	}
	account, ok := rig.store.FindByEmail("new@example.test")
	if !ok || account.ID != rig.account.ID || account.User.Email != "new@example.test" {
		t.Fatal("new normalized email does not resolve to the original account")
	}
	org := account.Organizations[account.ActiveOrganizationID]
	if org.Members[account.User.ID].Email != account.User.Email {
		t.Fatal("membership email did not follow the account")
	}
	if _, ok := rig.sessions.Principal(func() *http.Request {
		request := httptest.NewRequest(http.MethodGet, "http://dashboard.test/profile", nil)
		request.AddCookie(rig.sessionCookie)
		return request
	}()); !ok {
		t.Fatal("email change invalidated the existing session")
	}
	if err := rig.store.ChangeEmailAs(account.Principal(), "another@example.test", "stale-hash"); err != core.ErrConflict {
		t.Fatalf("stale credential changed email: %v", err)
	}
}

func TestDemoSecurityStateIsScopedAndOneTime(t *testing.T) {
	rig := newProfileTestRig(t)
	locale := rig.post("/api/v1/dashboard/profile/language", url.Values{"locale": {"pt-BR"}})
	if locale.Code != http.StatusSeeOther {
		t.Fatalf("language status = %d", locale.Code)
	}
	account, _ := rig.store.Snapshot(rig.account.ID)
	if account.User.Locale != "pt-BR" {
		t.Fatal("language preference was not saved")
	}
	rig.post("/api/v1/dashboard/profile/language", url.Values{"locale": {"unknown"}})
	account, _ = rig.store.Snapshot(rig.account.ID)
	if account.User.Locale != "pt-BR" {
		t.Fatal("unsupported locale changed the account")
	}
	path := "/api/v1/dashboard/profile/mfa/enroll"
	wrong := rig.post(path, url.Values{"password": {"wrong-password"}})
	if wrong.Code != http.StatusSeeOther || !strings.Contains(wrong.Header().Get("Location"), "error=") {
		t.Fatalf("wrong password enrolled MFA: %d", wrong.Code)
	}
	started := rig.post(path, url.Values{"password": {profileTestPassword}})
	location, err := url.Parse(started.Header().Get("Location"))
	if started.Code != http.StatusSeeOther || err != nil || location.Query().Get("enroll") == "" {
		t.Fatalf("demo enrollment did not start: %d", started.Code)
	}
	token := location.Query().Get("enroll")
	account, _ = rig.store.Snapshot(rig.account.ID)
	if account.DemoSecurity.PendingTOTPToken != token || account.DemoSecurity.PendingTOTPSecret == "" {
		t.Fatal("pending enrollment state is incomplete")
	}
	if response := rig.get(started.Header().Get("Location")); response.Code != http.StatusOK {
		t.Fatalf("first enrollment page returned %d", response.Code)
	}
	account, _ = rig.store.Snapshot(rig.account.ID)
	if account.DemoSecurity.PendingTOTPSecret != "" || account.DemoSecurity.PendingTOTPToken != token {
		t.Fatal("enrollment secret was not consumed exactly once")
	}
	invalid := rig.post("/api/v1/dashboard/profile/mfa/verify", url.Values{"enrollment": {token}, "code": {"abc123"}})
	if invalid.Code != http.StatusSeeOther || !strings.Contains(invalid.Header().Get("Location"), "error=") {
		t.Fatalf("invalid demo code was accepted: %d", invalid.Code)
	}
	verified := rig.post("/api/v1/dashboard/profile/mfa/verify", url.Values{"enrollment": {token}, "code": {"123456"}})
	if verified.Code != http.StatusSeeOther || !strings.Contains(verified.Header().Get("Location"), "status=mfa-added") {
		t.Fatalf("valid demo code was rejected: %d", verified.Code)
	}
	account, _ = rig.store.Snapshot(rig.account.ID)
	if !account.DemoSecurity.MFAEnabled || len(account.DemoSecurity.PendingRecoveryCodes) != 8 {
		t.Fatal("demo MFA or recovery state was not saved")
	}
	copyAccount := account
	copyAccount.DemoSecurity.PendingRecoveryCodes[0] = "overwritten"
	fresh, _ := rig.store.Snapshot(rig.account.ID)
	if fresh.DemoSecurity.PendingRecoveryCodes[0] == "overwritten" {
		t.Fatal("recovery code snapshot aliases store state")
	}
	if response := rig.get("/profile?tab=security"); response.Code != http.StatusOK {
		t.Fatalf("recovery reveal returned %d", response.Code)
	}
	fresh, _ = rig.store.Snapshot(rig.account.ID)
	if len(fresh.DemoSecurity.PendingRecoveryCodes) != 0 {
		t.Fatal("recovery codes were not consumed")
	}
	regenerated := rig.post("/api/v1/dashboard/profile/mfa/recovery-codes", url.Values{"password": {profileTestPassword}})
	if regenerated.Code != http.StatusSeeOther {
		t.Fatalf("regeneration returned %d", regenerated.Code)
	}
	fresh, _ = rig.store.Snapshot(rig.account.ID)
	if len(fresh.DemoSecurity.PendingRecoveryCodes) != 8 {
		t.Fatal("recovery codes were not regenerated")
	}
	removed := rig.post("/api/v1/dashboard/profile/mfa/remove", url.Values{"factor": {"totp"}, "password": {profileTestPassword}})
	if removed.Code != http.StatusSeeOther {
		t.Fatalf("removal returned %d", removed.Code)
	}
	fresh, _ = rig.store.Snapshot(rig.account.ID)
	if fresh.DemoSecurity.MFAEnabled || len(fresh.DemoSecurity.PendingRecoveryCodes) != 0 {
		t.Fatal("demo MFA removal left active state")
	}
}

func TestDemoPasskeysAndSessionsStayAccountScoped(t *testing.T) {
	rig := newProfileTestRig(t)
	added := rig.post("/api/v1/dashboard/profile/passkeys/register", url.Values{"name": {"Laptop"}})
	if added.Code != http.StatusSeeOther {
		t.Fatalf("passkey registration returned %d", added.Code)
	}
	account, _ := rig.store.Snapshot(rig.account.ID)
	if len(account.DemoSecurity.Passkeys) != 1 || account.DemoSecurity.Passkeys[0].Name != "Laptop" {
		t.Fatal("passkey display record missing")
	}
	id := account.DemoSecurity.Passkeys[0].ID
	account.DemoSecurity.Passkeys[0].Name = "changed"
	fresh, _ := rig.store.Snapshot(rig.account.ID)
	if fresh.DemoSecurity.Passkeys[0].Name != "Laptop" {
		t.Fatal("passkey snapshot aliases store state")
	}
	current := httptest.NewRequest(http.MethodGet, "http://dashboard.test/profile", nil)
	current.AddCookie(rig.sessionCookie)
	sessions := rig.sessions.SessionsForUser(rig.account.Principal(), current)
	if len(sessions) != 1 || !sessions[0].Current || sessions[0].ID == "" {
		t.Fatal("current session was not listed safely")
	}
	otherHash, err := auth.HashPassword("other-password-123")
	if err != nil {
		t.Fatal(err)
	}
	other, err := rig.store.CreateAccount(core.User{Name: "Other", Email: "other@example.test", PasswordHash: otherHash})
	if err != nil {
		t.Fatal(err)
	}
	otherResponse := httptest.NewRecorder()
	otherRequest := httptest.NewRequest(http.MethodGet, "http://dashboard.test/profile", nil)
	if err := rig.sessions.Start(otherResponse, otherRequest, other.Principal()); err != nil {
		t.Fatal(err)
	}
	if got := rig.sessions.SessionsForUser(rig.account.Principal(), current); len(got) != 1 {
		t.Fatal("another account session leaked into the list")
	}
	removed := rig.post("/api/v1/dashboard/profile/passkeys/remove", url.Values{"passkeyId": {id}})
	if removed.Code != http.StatusSeeOther {
		t.Fatalf("passkey removal returned %d", removed.Code)
	}
	fresh, _ = rig.store.Snapshot(rig.account.ID)
	if len(fresh.DemoSecurity.Passkeys) != 0 {
		t.Fatal("passkey record was not removed")
	}
	request := httptest.NewRequest(http.MethodPost, "http://dashboard.test/api/v1/dashboard/profile/passkeys/register", strings.NewReader("name=Other"))
	request.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	request.Header.Set("Origin", "http://dashboard.test")
	request.AddCookie(rig.sessionCookie)
	response := httptest.NewRecorder()
	rig.handler.ServeHTTP(response, request)
	if response.Code != http.StatusForbidden {
		t.Fatalf("mutation without CSRF returned %d", response.Code)
	}
}
