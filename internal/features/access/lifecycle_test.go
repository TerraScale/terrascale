package access_test

import (
	"errors"
	"io"
	"io/fs"
	"net/http"
	"net/http/cookiejar"
	"net/http/httptest"
	"net/url"
	"regexp"
	"strings"
	"testing"
	"time"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/features/access"
	"terrascale.tech/dashboard/internal/features/organization"
	"terrascale.tech/dashboard/internal/platform/csrf"
	"terrascale.tech/dashboard/internal/platform/datastar"
	"terrascale.tech/dashboard/internal/platform/render"
	"terrascale.tech/dashboard/internal/platform/session"
	"terrascale.tech/dashboard/internal/ui"
)

type lifecycleApp struct {
	server         *httptest.Server
	store          *core.Store
	sessions       *session.Manager
	organizations  organization.Service
	access         access.Service
	rendererErrors *rendererErrors
	owner          core.Account
	member         core.Account
	organization   core.Organization
}

type rendererErrors struct {
	core.Renderer
	pageError error
}

func (r *rendererErrors) Page(w http.ResponseWriter, request *http.Request, page core.Page) error {
	err := r.Renderer.Page(w, request, page)
	r.pageError = err
	return err
}

func newLifecycleApp(t *testing.T) lifecycleApp {
	t.Helper()
	store := core.NewStore()
	owner, err := store.CreateAccount(core.User{Name: "Owner", Email: "owner@example.test", PasswordHash: "hash"})
	if err != nil {
		t.Fatal(err)
	}
	member, err := store.CreateAccount(core.User{Name: "Member", Email: "member@example.test", PasswordHash: "hash"})
	if err != nil {
		t.Fatal(err)
	}
	organizations := organization.NewService(store)
	org, err := organizations.CreateBusiness(owner.Principal(), "Example Business")
	if err != nil {
		t.Fatalf("CreateBusiness() error = %v", err)
	}
	owner, _ = store.Snapshot(owner.ID)
	_, token, err := organizations.CreateInvitation(owner.Principal(), member.User.Email, "member")
	if err != nil {
		t.Fatalf("CreateInvitation() error = %v", err)
	}
	accessService := access.NewService(store)
	if _, err := accessService.RespondToInvitation(member.Principal(), token, "accept"); err != nil {
		t.Fatalf("RespondToInvitation(accept) error = %v", err)
	}
	member, _ = store.Snapshot(member.ID)

	sessions := session.New(store, session.Options{CookieName: "ts_session", Lifetime: time.Hour})
	guard := csrf.New(sessions, csrf.Options{CookieName: "ts_csrf"})
	layouts, err := fs.Sub(ui.Assets, "layouts")
	if err != nil {
		t.Fatalf("fs.Sub(ui.Assets): %v", err)
	}
	renderer, err := render.New(layouts, store, guard)
	if err != nil {
		t.Fatalf("render.New(): %v", err)
	}
	capturedRenderer := &rendererErrors{Renderer: renderer}
	deps := core.Deps{Store: store, Sessions: sessions, CSRF: guard, Render: capturedRenderer, Events: datastar.Events{}, Demo: true}
	mux := http.NewServeMux()
	organization.Mount(mux, deps)
	access.Mount(mux, deps)
	server := httptest.NewServer(guard.Protect(mux))
	t.Cleanup(server.Close)
	return lifecycleApp{
		server: server, store: store, sessions: sessions, organizations: organizations, access: accessService,
		rendererErrors: capturedRenderer, owner: owner, member: member, organization: org,
	}
}

func (app lifecycleApp) client(t *testing.T, account core.Account) *http.Client {
	t.Helper()
	jar, err := cookiejar.New(nil)
	if err != nil {
		t.Fatal(err)
	}
	response := httptest.NewRecorder()
	request := httptest.NewRequest(http.MethodGet, app.server.URL+"/", nil)
	if err := app.sessions.Start(response, request, account.Principal()); err != nil {
		t.Fatalf("start %s session: %v", account.User.Email, err)
	}
	serverURL, err := url.Parse(app.server.URL)
	if err != nil {
		t.Fatal(err)
	}
	jar.SetCookies(serverURL, response.Result().Cookies())
	return &http.Client{Jar: jar, CheckRedirect: func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse }}
}

func getBody(t *testing.T, client *http.Client, baseURL, path string) (int, string) {
	t.Helper()
	response, err := client.Get(baseURL + path)
	if err != nil {
		t.Fatalf("GET %s: %v", path, err)
	}
	defer response.Body.Close()
	body, err := io.ReadAll(response.Body)
	if err != nil {
		t.Fatalf("read GET %s: %v", path, err)
	}
	return response.StatusCode, string(body)
}

var lifecycleCSRFPattern = regexp.MustCompile(`name="_csrf" value="([^"]+)"`)

func postForm(t *testing.T, client *http.Client, baseURL, page, path string, values url.Values) *http.Response {
	t.Helper()
	status, body := getBody(t, client, baseURL, page)
	if status != http.StatusOK {
		t.Fatalf("GET %s before POST: status = %d", page, status)
	}
	match := lifecycleCSRFPattern.FindStringSubmatch(body)
	if len(match) != 2 {
		t.Fatalf("GET %s rendered no CSRF token", page)
	}
	copy := make(url.Values, len(values)+1)
	for key, entries := range values {
		copy[key] = append([]string(nil), entries...)
	}
	copy.Set("_csrf", match[1])
	response, err := client.PostForm(baseURL+path, copy)
	if err != nil {
		t.Fatalf("POST %s: %v", path, err)
	}
	return response
}

func TestLeaveOrganizationRevokesScopeAndAllowsReinvitation(t *testing.T) {
	app := newLifecycleApp(t)
	ownerClient := app.client(t, app.owner)
	memberClient := app.client(t, app.member)
	oldMemberScope := app.member.Principal()

	if status, body := getBody(t, ownerClient, app.server.URL, "/organization"); status != http.StatusOK || strings.Contains(body, "Leave organization") {
		t.Fatalf("owner organization page status=%d, leave action visible=%v", status, strings.Contains(body, "Leave organization"))
	}
	if status, body := getBody(t, memberClient, app.server.URL, "/organization"); status != http.StatusOK || !strings.Contains(body, "Leave organization") {
		t.Fatalf("member organization page status=%d, leave action visible=%v", status, strings.Contains(body, "Leave organization"))
	}
	if status, body := getBody(t, ownerClient, app.server.URL, "/access"); status != http.StatusOK || !strings.Contains(body, "Example Business") {
		t.Fatalf("owner access page status=%d, organization rendered=%v, render error=%v", status, strings.Contains(body, "Example Business"), app.rendererErrors.pageError)
	}
	if status, body := getBody(t, memberClient, app.server.URL, "/access"); status != http.StatusOK || !strings.Contains(body, "member@example.test") {
		t.Fatalf("member access page status=%d, member rendered=%v", status, strings.Contains(body, "member@example.test"))
	}

	response := postForm(t, memberClient, app.server.URL, "/organization", "/api/v1/dashboard/organization/leave", url.Values{})
	response.Body.Close()
	if response.StatusCode != http.StatusSeeOther || response.Header.Get("Location") != "/organization" {
		t.Fatalf("leave status/location = %d %q, want redirect to /organization", response.StatusCode, response.Header.Get("Location"))
	}
	memberAfterLeave, ok := app.store.Snapshot(app.member.ID)
	if !ok || memberAfterLeave.ActiveOrganizationID == app.organization.ID {
		t.Fatal("leaving member did not return to an accessible organization")
	}
	if _, exists := memberAfterLeave.Organizations[app.organization.ID]; exists {
		t.Fatal("leaving member retained a local reference to the shared organization")
	}
	if _, err := app.store.SnapshotAs(oldMemberScope); !errors.Is(err, core.ErrForbidden) && !errors.Is(err, core.ErrNotFound) {
		t.Fatalf("old organization scope error = %v, want denied", err)
	}
	if status, body := getBody(t, memberClient, app.server.URL, "/organization"); status != http.StatusOK || strings.Contains(body, "Leave organization") {
		t.Fatalf("post-leave organization page status=%d, leave action visible=%v, render error=%v", status, strings.Contains(body, "Leave organization"), app.rendererErrors.pageError)
	}

	ownerAfterLeave, _ := app.store.Snapshot(app.owner.ID)
	_, token, err := app.organizations.CreateInvitation(ownerAfterLeave.Principal(), app.member.User.Email, "viewer")
	if err != nil {
		t.Fatalf("re-invite after leaving: %v", err)
	}
	link := "/invitations/" + url.PathEscape(token)
	if status, body := getBody(t, memberClient, app.server.URL, link); status != http.StatusOK || !strings.Contains(body, "Join Example Business") {
		t.Fatalf("invitation page status=%d, organization rendered=%v", status, strings.Contains(body, "Join Example Business"))
	}
	response = postForm(t, memberClient, app.server.URL, link, link+"/respond", url.Values{"action": {"accept"}})
	response.Body.Close()
	if response.StatusCode != http.StatusSeeOther || response.Header.Get("Location") != "/access?tab=members&accepted=1" {
		t.Fatalf("accept status/location = %d %q", response.StatusCode, response.Header.Get("Location"))
	}
	memberAfterRejoin, _ := app.store.Snapshot(app.member.ID)
	rejoined, err := app.store.SnapshotAs(memberAfterRejoin.Principal())
	if err != nil || rejoined.Organizations[app.organization.ID].Members[app.member.User.ID].Role != "viewer" {
		t.Fatalf("rejoined member scope/role = %q/%q, error = %v", memberAfterRejoin.ActiveOrganizationID, rejoined.Organizations[app.organization.ID].Members[app.member.User.ID].Role, err)
	}
	if status, _ := getBody(t, memberClient, app.server.URL, "/access"); status != http.StatusOK {
		t.Fatalf("rejoined member access page status = %d, want 200", status)
	}
}

func TestInvitationDeclineAndEmailBinding(t *testing.T) {
	store := core.NewStore()
	owner, err := store.CreateAccount(core.User{Name: "Owner", Email: "owner@example.test", PasswordHash: "hash"})
	if err != nil {
		t.Fatal(err)
	}
	invitee, err := store.CreateAccount(core.User{Name: "Invitee", Email: "invitee@example.test", PasswordHash: "hash"})
	if err != nil {
		t.Fatal(err)
	}
	other, err := store.CreateAccount(core.User{Name: "Other", Email: "other@example.test", PasswordHash: "hash"})
	if err != nil {
		t.Fatal(err)
	}
	organizations := organization.NewService(store)
	if _, err := organizations.CreateBusiness(owner.Principal(), "Example Business"); err != nil {
		t.Fatal(err)
	}
	owner, _ = store.Snapshot(owner.ID)
	_, token, err := organizations.CreateInvitation(owner.Principal(), invitee.User.Email, "viewer")
	if err != nil {
		t.Fatal(err)
	}
	service := access.NewService(store)
	if _, err := service.RespondToInvitation(other.Principal(), token, "accept"); !errors.Is(err, core.ErrForbidden) {
		t.Fatalf("wrong-account invitation response error = %v, want forbidden", err)
	}
	declined, err := service.RespondToInvitation(invitee.Principal(), token, "decline")
	if err != nil {
		t.Fatalf("decline invitation: %v", err)
	}
	if got := declined.Invitations; len(got) != 1 {
		t.Fatalf("invitation count after decline = %d, want 1", len(got))
	} else {
		for _, invitation := range got {
			if invitation.Status != "declined" {
				t.Fatalf("declined invitation status = %q", invitation.Status)
			}
		}
	}
	if _, err := service.RespondToInvitation(invitee.Principal(), token, "accept"); !errors.Is(err, core.ErrConflict) {
		t.Fatalf("response to declined invitation error = %v, want conflict", err)
	}
	if _, _, err := organizations.CreateInvitation(owner.Principal(), invitee.User.Email, "member"); err != nil {
		t.Fatalf("re-invite after decline: %v", err)
	}
}

func TestDatastarMemberFilterStreamsSafeFragmentAndKeepsNativeGet(t *testing.T) {
	app := newLifecycleApp(t)
	ownerAccount, ok := app.store.Snapshot(app.owner.ID)
	if !ok {
		t.Fatal("owner account disappeared")
	}
	const unsafeName = `Member <img src=x onerror=alert(1)>`
	if err := app.store.UpdateAs(ownerAccount.Principal(), func(a *core.Account) error {
		org := a.Organizations[ownerAccount.ActiveOrganizationID]
		member := org.Members[app.member.User.ID]
		member.Name = unsafeName
		org.Members[member.UserID] = member
		a.Organizations[org.ID] = org
		return nil
	}); err != nil {
		t.Fatalf("set HTML-like member name: %v", err)
	}
	client := app.client(t, ownerAccount)
	filterURL := "/access?tab=members&q=onerror&role=member"

	regularRequest, err := http.NewRequest(http.MethodGet, app.server.URL+filterURL, nil)
	if err != nil {
		t.Fatal(err)
	}
	regularResponse, err := client.Do(regularRequest)
	if err != nil {
		t.Fatalf("regular member-filter GET: %v", err)
	}
	regularBody, err := io.ReadAll(regularResponse.Body)
	regularResponse.Body.Close()
	if err != nil {
		t.Fatalf("read regular member-filter GET: %v", err)
	}
	if regularResponse.StatusCode != http.StatusOK || !strings.HasPrefix(regularResponse.Header.Get("Content-Type"), "text/html") {
		t.Fatalf("regular filter response = %d %q, want 200 text/html", regularResponse.StatusCode, regularResponse.Header.Get("Content-Type"))
	}
	if !strings.Contains(string(regularBody), "<!doctype html") || !strings.Contains(string(regularBody), `value="onerror"`) {
		t.Fatal("ordinary filter GET did not return the full page with the submitted search value")
	}
	csrfMatch := lifecycleCSRFPattern.FindSubmatch(regularBody)
	if len(csrfMatch) != 2 {
		t.Fatal("ordinary filter GET did not render a CSRF token")
	}

	datastarRequest, err := http.NewRequest(http.MethodGet, app.server.URL+filterURL, nil)
	if err != nil {
		t.Fatal(err)
	}
	datastarRequest.Header.Set("Datastar-Request", "true")
	datastarResponse, err := client.Do(datastarRequest)
	if err != nil {
		t.Fatalf("Datastar member-filter GET: %v", err)
	}
	datastarBody, err := io.ReadAll(datastarResponse.Body)
	datastarResponse.Body.Close()
	if err != nil {
		t.Fatalf("read Datastar member-filter GET: %v", err)
	}
	if datastarResponse.StatusCode != http.StatusOK || !strings.HasPrefix(datastarResponse.Header.Get("Content-Type"), "text/event-stream") {
		t.Fatalf("Datastar response = %d %q, want 200 text/event-stream", datastarResponse.StatusCode, datastarResponse.Header.Get("Content-Type"))
	}
	stream := string(datastarBody)
	if !strings.Contains(stream, "event: datastar-patch-elements") || !strings.Contains(stream, `<main id="access-page"`) {
		t.Fatalf("Datastar response did not patch the stable access root: %q", stream)
	}
	if strings.Contains(stream, `<img src=x`) || !strings.Contains(stream, `&lt;img src=x onerror=alert(1)&gt;`) {
		t.Fatalf("member name was not HTML escaped in the fragment: %q", stream)
	}
	if !strings.Contains(stream, `value="onerror"`) || !strings.Contains(stream, string(csrfMatch[1])) {
		t.Fatal("Datastar fragment did not preserve filter input and CSRF form fields")
	}

	unauthenticated := &http.Client{CheckRedirect: func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse }}
	unauthenticatedRequest, err := http.NewRequest(http.MethodGet, app.server.URL+filterURL, nil)
	if err != nil {
		t.Fatal(err)
	}
	unauthenticatedRequest.Header.Set("Datastar-Request", "true")
	unauthenticatedResponse, err := unauthenticated.Do(unauthenticatedRequest)
	if err != nil {
		t.Fatalf("unauthenticated Datastar GET: %v", err)
	}
	unauthenticatedResponse.Body.Close()
	if unauthenticatedResponse.StatusCode != http.StatusSeeOther || unauthenticatedResponse.Header.Get("Location") != "/login" || strings.HasPrefix(unauthenticatedResponse.Header.Get("Content-Type"), "text/event-stream") {
		t.Fatalf("unauthenticated Datastar response = %d %q %q, want login redirect", unauthenticatedResponse.StatusCode, unauthenticatedResponse.Header.Get("Location"), unauthenticatedResponse.Header.Get("Content-Type"))
	}
}
