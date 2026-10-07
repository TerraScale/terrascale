package e2e

import (
	json "encoding/json/v2"
	"net/http"
	"net/url"
	"slices"
	"strings"
	"testing"

	"github.com/mxschmitt/playwright-go"

	"terrascale.tech/dashboard/internal/features/apikeys"
)

type listedDatabase struct {
	ID     string `json:"id"`
	Name   string `json:"name"`
	Status string `json:"status"`
}

func TestFreshAccountRegistrationOnboardingSessionAndSignIn(t *testing.T) {
	t.Parallel()
	account := newAccount("journey")
	page := newPage(t)

	goTo(t, page, "/login")
	highlightedMap := page.Locator(`.ts-region-map[data-highlights="SFO,GRU,FRA"]`)
	must(t, expect.Locator(highlightedMap).ToHaveAttribute("aria-hidden", "true"))
	must(t, expect.Locator(highlightedMap.Locator(".ts-region-node")).ToHaveCount(3))
	mapImage := highlightedMap.Locator("img.ts-region-map-image")
	must(t, expect.Locator(mapImage).ToHaveAttribute("src", assetURL("/brand/region-map.svg")))
	must(t, expect.Locator(mapImage).ToHaveAttribute("loading", "lazy"))
	screenshot(t, page, "login")

	password := page.Locator("#password")
	toggle := page.Locator(`button[data-password-toggle][aria-controls="password"]`)
	must(t, expect.Locator(toggle).ToBeHidden())
	fill(t, password, "reveal-control-check")
	visible(t, toggle)
	must(t, expect.Locator(toggle).ToHaveAccessibleName("Show password"))
	click(t, toggle)
	must(t, expect.Locator(password).ToHaveAttribute("type", "text"))
	must(t, expect.Locator(toggle).ToHaveAttribute("aria-pressed", "true"))
	must(t, expect.Locator(toggle).ToHaveAccessibleName("Hide password"))
	must(t, toggle.Press("Space"))
	must(t, expect.Locator(password).ToHaveAttribute("type", "password"))
	must(t, expect.Locator(toggle).ToHaveAttribute("aria-pressed", "false"))
	must(t, expect.Locator(toggle).ToHaveAccessibleName("Show password"))
	must(t, password.Clear())
	must(t, expect.Locator(toggle).ToBeHidden())

	goTo(t, page, "/onboarding")
	screenshot(t, page, "onboarding")
	registerAndOnboard(t, page, account)

	screenshot(t, page, "dashboard")
	visible(t, pageByRole(page, *playwright.AriaRoleHeading, "Overview"))
	visible(t, page.GetByText(account.DisplayName).First())

	var me struct {
		User struct {
			Email               string `json:"email"`
			Name                string `json:"name"`
			OnboardingCompleted bool   `json:"onboardingCompleted"`
		} `json:"user"`
	}
	responseJSON(t, apiGet(t, page.Request(), "/api/v1/auth/me", ""), 200, &me)
	if me.User.Email != strings.ToLower(account.Email) || me.User.Name != account.DisplayName || !me.User.OnboardingCompleted {
		t.Fatalf("GET /api/v1/auth/me user = %+v, want email %q, name %q, onboarding completed",
			me.User, strings.ToLower(account.Email), account.DisplayName)
	}

	logout(t, page)
	login(t, page, account.Email, account.Password)
	visible(t, pageByRole(page, *playwright.AriaRoleHeading, "Overview"))
}

func TestDatabaseAndProjectKeyStayInsideOwningAccount(t *testing.T) {
	t.Parallel()
	ownerPage := newPage(t)
	registerAndOnboard(t, ownerPage, newAccount("tenant-a"))
	goTo(t, ownerPage, "/databases")
	projectID := requiredAttribute(t, ownerPage.Locator("main[data-project-id]"), "data-project-id",
		"database page must expose its selected project identity")

	databaseName := uniqueName("e2e")
	createDatabaseFromUI(t, ownerPage, databaseName)
	visible(t, pageByRole(ownerPage, *playwright.AriaRoleHeading, databaseName))
	database := listedDatabaseNamed(t, ownerPage, databaseName)

	session := sessionToken(t, ownerPage.Context())
	keyPath := "/api/v1/projects/" + projectID + "/auth/api-keys"
	created := issueAPIKey(t, ownerPage, session, keyPath, "journey key", database.ID, "database:read")
	if !strings.HasPrefix(created.Secret, "tsk_demo_") {
		t.Errorf("demo API key secret %q must start with tsk_demo_", created.Secret)
	}
	if key := created.APIKey; key.DatabaseID != database.ID || !slices.Equal(key.Scopes, []string{"database:read"}) || !key.Active {
		t.Errorf("created API key = %+v, want database %q, scopes [database:read], active", key, database.ID)
	}

	var listed struct {
		APIKeys []apikeys.ProjectAPIKey `json:"apiKeys"`
	}
	responseJSON(t, apiGet(t, ownerPage.Request(), keyPath, session), 200, &listed)
	if len(listed.APIKeys) != 1 || listed.APIKeys[0].Name != "journey key" {
		t.Errorf("listed API keys = %+v, want exactly the journey key", listed.APIKeys)
	}

	otherPage := newPage(t)
	registerAndOnboard(t, otherPage, newAccount("tenant-b"))
	goTo(t, otherPage, "/databases")
	var otherDatabases []listedDatabase
	responseJSON(t, apiGet(t, otherPage.Request(), "/api/v1/dashboard/databases", ""), 200, &otherDatabases)
	if otherDatabases == nil || len(otherDatabases) != 0 {
		t.Errorf("another account's databases = %+v, want an empty list", otherDatabases)
	}

	otherSession := sessionToken(t, otherPage.Context())
	expectStatus(t, apiGet(t, otherPage.Request(), keyPath, otherSession), 404)
	expectStatus(t, apiGet(t, newContext(t, baseURL).Request(), keyPath, ""), 401)
}

// createDatabaseFromUI submits the create-database dialog and waits for the
// new database page.
func createDatabaseFromUI(t *testing.T, page playwright.Page, name string) {
	t.Helper()
	click(t, pageByRole(page, *playwright.AriaRoleButton, "New database").First())
	dialog := pageByRole(page, *playwright.AriaRoleDialog, "New database")
	visible(t, dialog)
	form := dialog.Locator(`form[action="/api/v1/dashboard/databases"]`)
	fill(t, form.Locator(`[name="name"]`), name)
	selectDatabaseRegions(t, form, "GRU")
	click(t, byRole(form, *playwright.AriaRoleButton, "Create database"))
	waitForPath(t, page, func(u *url.URL) bool { return strings.HasPrefix(u.Path, "/databases/db_") })
}

// listedDatabaseNamed returns the named database from the dashboard list API.
func listedDatabaseNamed(t *testing.T, page playwright.Page, name string) listedDatabase {
	t.Helper()
	var databases []listedDatabase
	responseJSON(t, apiGet(t, page.Request(), "/api/v1/dashboard/databases", ""), 200, &databases)
	index := slices.IndexFunc(databases, func(d listedDatabase) bool { return d.Name == name })
	if index < 0 {
		t.Fatalf("created database %q must remain listed for its owning account; got %+v", name, databases)
	}
	return databases[index]
}

// issueAPIKey creates a database-scoped project API key with the dashboard
// session as bearer credential.
func issueAPIKey(t *testing.T, page playwright.Page, session, keyPath, name, databaseID string, scopes ...string) apikeys.ProjectAPIKeySecret {
	t.Helper()
	response, err := page.Request().Post(keyPath, playwright.APIRequestContextPostOptions{
		Headers: map[string]string{"Authorization": "Bearer " + session},
		Data:    map[string]any{"name": name, "scopes": scopes, "databaseId": databaseID},
	})
	must(t, err)
	var secret apikeys.ProjectAPIKeySecret
	responseJSON(t, response, 201, &secret)
	return secret
}

func apiGet(t *testing.T, request playwright.APIRequestContext, path, bearer string) playwright.APIResponse {
	t.Helper()
	var options playwright.APIRequestContextGetOptions
	if bearer != "" {
		options.Headers = map[string]string{"Authorization": "Bearer " + bearer}
	}
	response, err := request.Get(path, options)
	must(t, err)
	return response
}

func expectStatus(t *testing.T, response playwright.APIResponse, status int) []byte {
	t.Helper()
	body, err := response.Body()
	must(t, err)
	if response.Status() != status {
		t.Fatalf("%s = %d, want %d: %s", response.URL(), response.Status(), status, body)
	}
	return body
}

func responseJSON(t *testing.T, response playwright.APIResponse, status int, target any) {
	t.Helper()
	body := expectStatus(t, response, status)
	if err := json.Unmarshal(body, target); err != nil {
		t.Fatalf("decode %s: %v: %s", response.URL(), err, body)
	}
}

func TestLandingRegistrationLinkOpensSignup(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	const link = "/register?plan=pro&return=%2Fpricing"
	response, err := page.Request().Get(link, playwright.APIRequestContextGetOptions{MaxRedirects: new(0)})
	must(t, err)
	if response.Status() != http.StatusSeeOther || response.Headers()["location"] != "/onboarding?plan=pro&return=%2Fpricing" {
		t.Fatalf("GET %s = %d, Location %q", link, response.Status(), response.Headers()["location"])
	}

	goTo(t, page, link)
	waitForPath(t, page, func(u *url.URL) bool {
		return u.Path == "/onboarding" && u.RawQuery == "plan=pro&return=%2Fpricing"
	})
	visible(t, page.Locator(`form[action="/onboarding"]`))
}
