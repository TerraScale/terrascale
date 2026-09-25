package organization_test

import (
	"io"
	"net/http"
	"net/http/cookiejar"
	"net/http/httptest"
	"net/url"
	"regexp"
	"strings"
	"testing"
	"time"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/features/organization"
	"terrascale.tech/dashboard/internal/features/subscriptions"
	"terrascale.tech/dashboard/internal/platform/csrf"
	"terrascale.tech/dashboard/internal/platform/render"
	"terrascale.tech/dashboard/internal/platform/session"
)

func TestBusinessWizardCheckoutRestoresPostedOrganization(t *testing.T) {
	store := core.NewStore()
	owner, err := store.CreateAccount(core.User{Name: "Owner", Email: "owner@example.test", PasswordHash: "hash"})
	if err != nil {
		t.Fatal(err)
	}
	sessions := session.New(store, session.Options{CookieName: "ts_session", Lifetime: time.Hour})
	guard := csrf.New(sessions, csrf.Options{CookieName: "ts_csrf"})
	deps := core.Deps{Store: store, Sessions: sessions, CSRF: guard, Render: render.New(store, guard), Demo: true}
	mux := http.NewServeMux()
	organization.Mount(mux, deps)
	subscriptions.Mount(mux, deps)
	server := httptest.NewServer(guard.Protect(mux))
	t.Cleanup(server.Close)

	jar, err := cookiejar.New(nil)
	if err != nil {
		t.Fatal(err)
	}
	serverURL, err := url.Parse(server.URL)
	if err != nil {
		t.Fatal(err)
	}
	w := httptest.NewRecorder()
	r := httptest.NewRequest(http.MethodGet, server.URL+"/", nil)
	if err := sessions.Start(w, r, owner.Principal()); err != nil {
		t.Fatal(err)
	}
	jar.SetCookies(serverURL, w.Result().Cookies())
	client := &http.Client{Jar: jar, CheckRedirect: func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse }}
	csrfPattern := regexp.MustCompile(`name="_csrf" value="([^"]+)"`)
	post := func(page, path string, values url.Values) *http.Response {
		t.Helper()
		pageResponse, err := client.Get(server.URL + page)
		if err != nil {
			t.Fatal(err)
		}
		body, err := io.ReadAll(pageResponse.Body)
		pageResponse.Body.Close()
		if err != nil || pageResponse.StatusCode != http.StatusOK {
			t.Fatalf("GET %s status/error = %d/%v", page, pageResponse.StatusCode, err)
		}
		match := csrfPattern.FindStringSubmatch(string(body))
		if len(match) != 2 {
			t.Fatalf("GET %s rendered no CSRF token", page)
		}
		values.Set("_csrf", match[1])
		response, err := client.PostForm(server.URL+path, values)
		if err != nil {
			t.Fatal(err)
		}
		return response
	}

	created := post("/organization?newOrganization=organization", "/api/v1/dashboard/organization/new-organization", url.Values{
		"step": {"organization"}, "name": {"Acme"},
	})
	created.Body.Close()
	if created.StatusCode != http.StatusSeeOther || created.Header.Get("Location") != "/organization?newOrganization=payment" {
		t.Fatalf("organization step status/location = %d %q", created.StatusCode, created.Header.Get("Location"))
	}
	account, _ := store.Snapshot(owner.ID)
	businessID := account.ActiveOrganizationID
	if businessID == owner.ActiveOrganizationID {
		t.Fatal("business organization was not selected")
	}

	blocked := post("/organization?newOrganization=project", "/api/v1/dashboard/organization/new-organization", url.Values{
		"step": {"project"}, "organizationID": {businessID}, "name": {"Database Project"},
		"region": {"GRU"}, "backupRegion": {"SFO"}, "regions": {"GRU", "SFO"},
	})
	blocked.Body.Close()
	if blocked.StatusCode != http.StatusSeeOther || !strings.HasPrefix(blocked.Header.Get("Location"), "/organization?newOrganization=payment") {
		t.Fatalf("project before billing status/location = %d %q", blocked.StatusCode, blocked.Header.Get("Location"))
	}

	switched := post("/organization", "/api/v1/dashboard/switch-organization", url.Values{
		"organizationId": {owner.ActiveOrganizationID},
	})
	switched.Body.Close()
	account, _ = store.Snapshot(owner.ID)
	if account.ActiveOrganizationID != owner.ActiveOrganizationID {
		t.Fatal("test did not switch back to Personal workspace")
	}

	var redirects []string
	client.CheckRedirect = func(request *http.Request, _ []*http.Request) error {
		redirects = append(redirects, request.Method+" "+request.URL.RequestURI())
		return nil
	}
	checkoutStart := post("/organization?newOrganization=organization", "/api/v1/dashboard/organization/new-organization", url.Values{
		"step": {"payment-method"}, "organizationID": {businessID}, "plan": {"pro"}, "interval": {"annual"},
	})
	client.CheckRedirect = func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse }
	checkoutStart.Body.Close()
	checkoutPath := checkoutStart.Request.URL.Path
	if checkoutStart.StatusCode != http.StatusOK || !strings.HasPrefix(checkoutPath, "/checkout/cs_local_") ||
		len(redirects) != 2 || !strings.HasPrefix(redirects[0], "POST /billing/checkout?return=") ||
		!strings.HasPrefix(redirects[1], "GET /checkout/cs_local_") {
		t.Fatalf("billing redirect chain = status %d, path %q, redirects %q", checkoutStart.StatusCode, checkoutPath, redirects)
	}
	account, _ = store.Snapshot(owner.ID)
	if account.ActiveOrganizationID != businessID || account.Organizations[owner.ActiveOrganizationID].Billing.CheckoutID != "" {
		t.Fatal("checkout was attached to the wrong organization")
	}

	finished := post(checkoutPath, checkoutPath+"/complete", url.Values{})
	finished.Body.Close()
	returnPath := finished.Header.Get("Location")
	expectedReturn := "/organization?newOrganization=project&organizationID=" + url.QueryEscape(businessID) + "&payment=success"
	if finished.StatusCode != http.StatusSeeOther || returnPath != expectedReturn {
		t.Fatalf("checkout result status/location = %d %q", finished.StatusCode, returnPath)
	}
	switched = post("/organization", "/api/v1/dashboard/switch-organization", url.Values{
		"organizationId": {owner.ActiveOrganizationID},
	})
	switched.Body.Close()
	returned, err := client.Get(server.URL + returnPath)
	if err != nil {
		t.Fatal(err)
	}
	returned.Body.Close()
	if returned.StatusCode != http.StatusOK {
		t.Fatalf("GET project wizard status = %d", returned.StatusCode)
	}
	account, _ = store.Snapshot(owner.ID)
	if account.ActiveOrganizationID != businessID {
		t.Fatal("checkout return did not restore the paid Business organization")
	}
	switched = post("/organization", "/api/v1/dashboard/switch-organization", url.Values{
		"organizationId": {owner.ActiveOrganizationID},
	})
	switched.Body.Close()
	project := post(returnPath, "/api/v1/dashboard/organization/new-organization", url.Values{
		"step": {"project"}, "organizationID": {businessID}, "name": {"Database Project"},
		"region": {"GRU"}, "backupRegion": {"SFO"}, "regions": {"GRU", "SFO"},
	})
	project.Body.Close()
	if project.StatusCode != http.StatusSeeOther || project.Header.Get("Location") != "/organization" {
		t.Fatalf("project step status/location = %d %q", project.StatusCode, project.Header.Get("Location"))
	}
	account, _ = store.Snapshot(owner.ID)
	if account.ActiveOrganizationID != businessID || account.ActiveProjectID == "" || len(account.Organizations[businessID].Projects) != 1 || len(account.Organizations[owner.ActiveOrganizationID].Projects) != 1 {
		t.Fatalf("business/personal project counts = %d/%d", len(account.Organizations[businessID].Projects), len(account.Organizations[owner.ActiveOrganizationID].Projects))
	}
}
