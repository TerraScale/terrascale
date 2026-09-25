package e2e

import (
	"cmp"
	"crypto/rand"
	"fmt"
	"net/http"
	"net/url"
	"os"
	"path/filepath"
	"regexp"
	"slices"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/mxschmitt/playwright-go"
)

type routeState struct {
	key, path, heading string
	status             int // expected document status; 0 means 200
	mobileHeight       int // mobile viewport height; 0 means 844
	anonymous          bool
}

var parityRouteStates = []routeState{
	{key: "overview", path: "/", heading: "Overview"},
	{key: "team", path: "/access", heading: "Team"},
	{key: "admin-databases", path: "/admin/databases", status: http.StatusForbidden},
	{key: "admin-estate", path: "/admin/estate", status: http.StatusForbidden},
	{key: "admin-usage", path: "/admin/usage", status: http.StatusForbidden},
	{key: "admin-users", path: "/admin/users", status: http.StatusForbidden},
	{key: "api-keys", path: "/api-keys", heading: "API keys"},
	{key: "audit", path: "/audit", heading: "Audit"},
	{key: "authentication", path: "/auth", heading: "Authentication"},
	{key: "auth-audit", path: "/auth/audit", heading: "Audit log"},
	{key: "auth-clients", path: "/auth/clients", heading: "OAuth clients"},
	{key: "auth-connections", path: "/auth/connections", heading: "Connections"},
	{key: "auth-keys", path: "/auth/keys", heading: "Management keys"},
	{key: "auth-memberships", path: "/auth/memberships", heading: "Memberships"},
	{key: "auth-organizations", path: "/auth/organizations", heading: "Organizations"},
	{key: "auth-quickstart", path: "/auth/quickstart", heading: "Connect in minutes"},
	{key: "auth-settings", path: "/auth/settings", heading: "Hosted UI branding"},
	{key: "auth-templates", path: "/auth/templates", heading: "JWT templates"},
	{key: "auth-users", path: "/auth/users", heading: "Users"},
	{key: "auth-webhooks", path: "/auth/webhooks", heading: "Webhooks"},
	{key: "backups", path: "/backups", heading: "Backups"},
	{key: "data", path: "/data", heading: "Saved queries"},
	{key: "data-create", path: "/data?create=1", heading: "Save query", mobileHeight: 900},
	{key: "data-editor", path: "/data?view=editor", heading: "Document editor", mobileHeight: 900},
	{key: "data-queries", path: "/data?view=queries", heading: "Saved queries", mobileHeight: 900},
	{key: "data-queries-create", path: "/data?view=queries&create=1", heading: "Save query", mobileHeight: 900},
	{key: "data-sql", path: "/data?view=sql", heading: "SQL console", mobileHeight: 900},
	{key: "data-table", path: "/data?view=table", heading: "Collection documents", mobileHeight: 900},
	{key: "databases", path: "/databases", heading: "Databases"},
	{key: "engine", path: "/engine", heading: "Engine"},
	{key: "history", path: "/history", heading: "Document history"},
	{key: "indexes", path: "/indexes", heading: "Indexes"},
	{key: "notifications", path: "/notifications", heading: "Notifications"},
	{key: "organization", path: "/organization", heading: "Organization"},
	{key: "profile", path: "/profile", heading: "Profile"},
	{key: "schema", path: "/schema", heading: "Schema"},
	{key: "settings", path: "/settings", heading: "Settings"},
	{key: "subscriptions", path: "/subscriptions", heading: "Shared result configuration"},
	{key: "usage", path: "/usage", heading: "Usage"},
	{key: "agent-approve", path: "/agent/approve", heading: "Approve agent access"},
	{key: "cli-approve", path: "/cli/approve", heading: "Approve CLI access"},
	{key: "login", path: "/login", heading: "Sign in", anonymous: true},
	{key: "onboarding", path: "/onboarding", heading: "Activate your TerraScale account", anonymous: true},
	{key: "reset-password", path: "/reset-password", heading: "Reset password"},
}

var deniedDocumentError = regexp.MustCompile(`^Failed to load resource: the server responded with a status of 403 \((?:Forbidden)?\)$`)

func TestPagesMemberNavigationKeyboardAndMobile(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	must(t, page.AddInitScript(playwright.Script{Content: new(`
		document.addEventListener("datastar-ready", () => {
			document.documentElement.dataset.datastarReady = "true";
		}, { once: true });`)}))
	registerAndOnboard(t, page, newAccount("navigation"))

	accountMenu := page.Locator("details.ts-account")
	accountTrigger := page.Locator(`details.ts-account > summary[aria-label="Account menu"]`)
	must(t, accountTrigger.Focus())
	must(t, expect.Locator(accountTrigger).ToBeFocused())

	navigation := pageByRole(page, *playwright.AriaRoleNavigation, "Dashboard sections")
	visible(t, byRole(navigation, *playwright.AriaRoleLink, "Overview"))
	for _, label := range []string{"Tables", "Team", "Usage", "Billing", "Settings"} {
		visible(t, byRole(navigation, *playwright.AriaRoleLink, label, true))
	}
	viewportScreenshot(t, page, "member-desktop-navigation")

	projectMenu := page.Locator("details.ts-context-menu")
	projectTrigger := page.Locator(`details.ts-context-menu > summary.ts-context-trigger[aria-label="Switch organization or project, current: Personal / Personal"]`)
	must(t, projectTrigger.Focus())
	must(t, page.Keyboard().Press("Enter"))
	must(t, expect.Locator(projectMenu).ToHaveAttribute("open", ""))
	must(t, expect.Locator(projectMenu.Locator("[data-active-project-id]")).ToContainText("Personal"))

	must(t, accountTrigger.Focus())
	must(t, page.Keyboard().Press("Enter"))
	must(t, expect.Locator(accountMenu).ToHaveAttribute("open", ""))
	visible(t, byRole(accountMenu, *playwright.AriaRoleLink, "Profile"))
	must(t, expect.Locator(byRole(accountMenu, *playwright.AriaRoleLink, "Administration")).ToHaveCount(0))
	must(t, page.Keyboard().Press("Escape"))
	must(t, expect.Locator(accountMenu).Not().ToHaveAttribute("open", ""))
	must(t, expect.Locator(accountTrigger).ToBeFocused())

	must(t, page.SetViewportSize(390, 844))
	must(t, expect.Locator(page.Locator("html")).ToHaveAttribute("data-datastar-ready", "true"))
	// Keep a stable DOM locator: opening the drawer hides the FAB's ancestor
	// from accessibility queries, while the FAB itself keeps its Open label.
	navigationToggle := page.Locator("button.ts-fab-nav")
	shell := page.Locator(".ts-shell")
	navOpen := regexp.MustCompile(`nav-open`)
	must(t, expect.Locator(navigationToggle).ToHaveAttribute("aria-label", "Open navigation"))
	must(t, expect.Locator(navigationToggle).ToHaveAttribute("aria-expanded", "false"))
	click(t, navigationToggle)
	must(t, expect.Locator(shell).ToHaveClass(navOpen))
	must(t, expect.Locator(navigationToggle).ToHaveAttribute("aria-label", "Open navigation"))
	must(t, expect.Locator(navigationToggle).ToHaveAttribute("aria-expanded", "true"))
	must(t, expect.Locator(pageByRole(page, *playwright.AriaRoleButton, "Close navigation")).ToBeFocused())
	viewportScreenshot(t, page, "member-mobile-navigation-open")

	drawer := pageByRole(page, *playwright.AriaRoleComplementary, "Primary navigation")
	must(t, page.Keyboard().Press("Tab"))
	must(t, expect.Locator(drawer).ToContainText("Build"))
	must(t, page.Keyboard().Press("Escape"))
	must(t, expect.Locator(shell).Not().ToHaveClass(navOpen))
	must(t, expect.Locator(navigationToggle).ToHaveAttribute("aria-label", "Open navigation"))
	must(t, expect.Locator(navigationToggle).ToHaveAttribute("aria-expanded", "false"))
	must(t, expect.Locator(navigationToggle).ToBeFocused())
	viewportScreenshot(t, page, "member-mobile-navigation-closed")

	goTo(t, page, "/databases")
	screenshot(t, page, "data")
	billing := goTo(t, page, "/billing")
	if billing.Status() != http.StatusOK || currentPath(t, page) != "/billing" {
		t.Fatalf("GET /billing = %d at %s", billing.Status(), page.URL())
	}
	screenshot(t, page, "billing")
}

func TestPagesRouteStatesRenderAtDesktopAndMobile(t *testing.T) {
	t.Parallel()
	context := newContext(t, baseURL)
	anonymous := newContext(t, baseURL)
	registerAndOnboard(t, deniedAwarePage(t, context, ""), newAccount("parity"))

	t.Run("routes", func(t *testing.T) {
		for _, route := range parityRouteStates {
			t.Run(route.key, func(t *testing.T) {
				t.Parallel()
				expectedStatus := cmp.Or(route.status, http.StatusOK)
				expectedURL, err := url.Parse(baseURL + route.path)
				must(t, err)
				routeContext, deniedURL := context, ""
				if route.anonymous {
					routeContext = anonymous
				}
				if expectedStatus == http.StatusForbidden {
					deniedURL = expectedURL.String()
				}
				page := deniedAwarePage(t, routeContext, deniedURL)

				mobileHeight := cmp.Or(route.mobileHeight, 844)
				for _, viewport := range []struct {
					name          string
					width, height int
				}{
					{"desktop-1440x900", 1440, 900},
					{fmt.Sprintf("mobile-390x%d", mobileHeight), 390, mobileHeight},
				} {
					must(t, page.SetViewportSize(viewport.width, viewport.height))
					response := goTo(t, page, route.path)
					if response.Status() != expectedStatus {
						t.Fatalf("%s returned %d, want %d", route.path, response.Status(), expectedStatus)
					}
					finalURL, err := url.Parse(page.URL())
					must(t, err)
					if finalURL.RequestURI() != expectedURL.RequestURI() {
						t.Fatalf("%s should remain on the requested route, got %s", route.path, finalURL.RequestURI())
					}
					if expectedStatus == http.StatusForbidden {
						if !response.Request().IsNavigationRequest() {
							t.Fatalf("%s should be a top-level denied navigation", route.path)
						}
						documentURL, err := url.Parse(response.URL())
						must(t, err)
						if documentURL.Path != expectedURL.Path {
							t.Fatalf("%s should be the denied document URL, got %s", route.path, documentURL.Path)
						}
					}
					if route.heading != "" {
						visible(t, pageByRole(page, *playwright.AriaRoleHeading, route.heading).First())
					}
					viewportScreenshot(t, page, "parity/"+route.key+"-"+viewport.name)
				}
			})
		}
	})

	page := deniedAwarePage(t, context, "")
	must(t, page.SetViewportSize(390, 900))
	response := goTo(t, page, "/data")
	if response.Status() != http.StatusOK || currentPath(t, page) != "/data" {
		t.Fatalf("GET /data = %d at %s", response.Status(), page.URL())
	}
	visible(t, pageByRole(page, *playwright.AriaRoleHeading, "Saved queries").First())
	viewportScreenshot(t, page, "parity/data-base-mobile-390x900")
}

func TestPagesRegionMapIsLazyAndNativeRegionControlsWork(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	registerAndOnboard(t, page, newAccount("region-map"))

	mapPage := watchedPage(t, cloneSession(t, page.Context(), true))
	var mapImageRequests atomic.Int64
	mapPage.OnRequest(func(request playwright.Request) {
		if parsed, err := url.Parse(request.URL()); err == nil && parsed.Path == "/brand/region-map.svg" {
			mapImageRequests.Add(1)
		}
	})

	goTo(t, mapPage, "/")
	visible(t, pageByRole(mapPage, *playwright.AriaRoleHeading, "Overview"))
	if n := mapImageRequests.Load(); n != 0 {
		t.Fatalf("overview requested the region map %d times, want 0", n)
	}

	goTo(t, mapPage, "/organization")
	click(t, mapPage.Locator("#create-project > summary"))
	regionMap := mapPage.Locator(`.ts-region-map[data-select-id="project-region"]`)
	must(t, expect.Locator(regionMap).ToHaveAttribute("aria-hidden", "true"))
	mapImage := regionMap.Locator("img")
	must(t, expect.Locator(mapImage).ToHaveAttribute("src", "/brand/region-map.svg"))
	must(t, expect.Locator(mapImage).ToHaveAttribute("loading", "lazy"))
	must(t, mapImage.ScrollIntoViewIfNeeded())
	eventually(t, "region map image request", func() bool { return mapImageRequests.Load() > 0 })

	nativePage := watchedPage(t, cloneSession(t, page.Context(), false))
	goTo(t, nativePage, "/organization")
	click(t, nativePage.Locator("#create-project > summary"))
	projectForm := nativePage.Locator(`form[action="/api/v1/dashboard/organization/projects"]`)
	region := projectForm.GetByLabel("Primary region")
	visible(t, region)
	regionOptions := region.Locator("option")
	count, err := regionOptions.Count()
	must(t, err)
	if count <= 1 {
		t.Fatalf("primary region offers %d options, want more than 1", count)
	}
	selectedRegion, err := regionOptions.Nth(1).GetAttribute("value")
	must(t, err)
	if selectedRegion == "" {
		t.Fatal("second region option must have a value")
	}
	selectOption(t, region, selectedRegion)
	must(t, expect.Locator(region).ToHaveValue(selectedRegion))
	projectName := "E2E Native Region " + rand.Text()[:8]
	fill(t, projectForm.Locator("#project-name"), projectName)

	createProject, err := nativePage.ExpectResponse(func(response playwright.Response) bool {
		parsed, err := url.Parse(response.URL())
		return err == nil && response.Request().Method() == http.MethodPost &&
			parsed.Path == "/api/v1/dashboard/organization/projects"
	}, func() error {
		return byRole(projectForm, *playwright.AriaRoleButton, "Create project", true).Click()
	})
	must(t, err)
	if createProject.Status() != http.StatusSeeOther {
		t.Fatalf("create project returned %d, want 303", createProject.Status())
	}
	waitForPath(t, nativePage, func(u *url.URL) bool { return u.Path == "/organization" })
	createdProject := nativePage.Locator("tbody tr").Filter(playwright.LocatorFilterOptions{HasText: projectName})
	visible(t, createdProject)
	click(t, createdProject.Locator("details.ts-rename > summary"))
	must(t, expect.Locator(createdProject.GetByLabel("Primary region")).ToHaveValue(selectedRegion))
}

func TestPagesMemberHasNoAdministration(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	registerAndOnboard(t, page, newAccount("member"))

	accountMenu := page.Locator("details.ts-account")
	click(t, page.Locator(`details.ts-account > summary[aria-label="Account menu"]`))
	must(t, expect.Locator(accountMenu).ToHaveAttribute("open", ""))
	must(t, expect.Locator(byRole(accountMenu, *playwright.AriaRoleLink, "Administration")).ToHaveCount(0))

	response, err := page.Request().Get("/admin", playwright.APIRequestContextGetOptions{MaxRedirects: new(0)})
	must(t, err)
	allowed := []int{http.StatusFound, http.StatusSeeOther, http.StatusUnauthorized, http.StatusForbidden, http.StatusNotFound}
	if !slices.Contains(allowed, response.Status()) {
		t.Fatalf("member GET /admin = %d, want one of %v", response.Status(), allowed)
	}
}

func TestPagesDemoAdministratorHasAdministration(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	login(t, page, admin.Email, admin.Password)

	accountMenu := page.Locator("details.ts-account")
	click(t, page.Locator(`details.ts-account > summary[aria-label="Account menu"]`))
	must(t, expect.Locator(accountMenu).ToHaveAttribute("open", ""))
	click(t, pageByRole(page, *playwright.AriaRoleLink, "Administration"))
	must(t, expect.Page(page).ToHaveURL(regexp.MustCompile(`/admin(?:/|$)`)))
	adminNavigation := pageByRole(page, *playwright.AriaRoleNavigation, "Admin sections")
	for _, label := range []string{"Users", "Organizations", "Plans"} {
		visible(t, byRole(adminNavigation, *playwright.AriaRoleLink, label, true))
	}
}

func TestPagesMemberFilterPatchesThroughDatastar(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	account := newAccount("datastar")
	account.DisplayName += " " + rand.Text()[:8]
	registerAndOnboard(t, page, account)

	pageResponse := goTo(t, page, "/access?tab=members")
	if pageResponse.Status() != http.StatusOK {
		t.Fatalf("GET /access?tab=members = %d", pageResponse.Status())
	}
	html, err := pageResponse.Text()
	must(t, err)
	match := regexp.MustCompile(`<html[^>]*data-nonce="([^"]+)"`).FindStringSubmatch(html)
	if match == nil {
		t.Fatal("the team page must expose its per-response script nonce")
	}
	if csp := pageResponse.Headers()["content-security-policy"]; !strings.Contains(csp, "'nonce-"+match[1]+"'") {
		t.Fatalf("content-security-policy %q does not allow nonce %q", csp, match[1])
	}

	_, err = page.Evaluate(`() => {
		window.__cspViolations = [];
		document.addEventListener("securitypolicyviolation", (event) => {
			window.__cspViolations.push(event.violatedDirective);
		});
	}`)
	must(t, err)
	var (
		mu          sync.Mutex
		navigations []string
	)
	page.OnFrameNavigated(func(frame playwright.Frame) {
		if frame == page.MainFrame() {
			mu.Lock()
			defer mu.Unlock()
			navigations = append(navigations, frame.URL())
		}
	})

	search := account.DisplayName
	memberSearch := page.Locator("#member-search")
	fill(t, memberSearch, search)
	patchResponse, err := page.ExpectResponse(func(response playwright.Response) bool {
		parsed, err := url.Parse(response.URL())
		request := response.Request()
		return err == nil && parsed.Path == "/access" && parsed.Query().Get("q") == search &&
			request.Method() == http.MethodGet && request.Headers()["datastar-request"] == "true"
	}, func() error { return memberSearch.Press("Enter") })
	must(t, err)

	patchRequest := patchResponse.Request()
	if patchRequest.IsNavigationRequest() {
		t.Fatal("the Datastar patch must not be a document navigation")
	}
	if accept := patchRequest.Headers()["accept"]; !strings.Contains(accept, "text/event-stream") {
		t.Fatalf("patch request accept = %q, want text/event-stream", accept)
	}
	assertMemberFilterQuery(t, patchRequest.URL(), search)
	if patchResponse.Status() != http.StatusOK {
		t.Fatalf("patch response status = %d", patchResponse.Status())
	}
	if contentType := patchResponse.Headers()["content-type"]; !strings.Contains(contentType, "text/event-stream") {
		t.Fatalf("patch response content-type = %q, want text/event-stream", contentType)
	}
	patch, err := patchResponse.Text()
	must(t, err)
	for _, want := range []string{"event: datastar-patch-elements", `id="access-page"`} {
		if !strings.Contains(patch, want) {
			t.Fatalf("patch body missing %q:\n%s", want, patch)
		}
	}

	must(t, expect.Locator(memberSearch).ToHaveValue(search))
	memberRows := page.Locator("#access-page tbody tr").Filter(playwright.LocatorFilterOptions{HasText: account.DisplayName})
	must(t, expect.Locator(memberRows).ToHaveCount(1))
	must(t, expect.Locator(page.Locator("#access-page")).ToHaveCount(1))
	mu.Lock()
	if len(navigations) > 0 {
		t.Errorf("unexpected document navigations: %v", navigations)
	}
	mu.Unlock()
	violations, err := page.Evaluate(`() => window.__cspViolations ?? []`)
	must(t, err)
	if list, _ := violations.([]any); len(list) > 0 {
		t.Fatalf("content security policy violations: %v", list)
	}
}

func TestPagesMemberFilterSubmitsNativelyWithoutJavaScript(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	account := newAccount("native-filter")
	account.DisplayName += " " + rand.Text()[:8]
	registerAndOnboard(t, page, account)

	nativePage := watchedPage(t, cloneSession(t, page.Context(), false))
	goTo(t, nativePage, "/access?tab=members")

	search := account.DisplayName
	memberSearch := nativePage.Locator("#member-search")
	fill(t, memberSearch, search)
	navigation, err := nativePage.ExpectResponse(func(response playwright.Response) bool {
		parsed, err := url.Parse(response.URL())
		return err == nil && parsed.Path == "/access" && parsed.Query().Get("q") == search &&
			response.Request().IsNavigationRequest()
	}, func() error { return memberSearch.Press("Enter") })
	must(t, err)

	if navigation.Status() != http.StatusOK {
		t.Fatalf("native filter navigation status = %d", navigation.Status())
	}
	if contentType := navigation.Headers()["content-type"]; !strings.Contains(contentType, "text/html") {
		t.Fatalf("native filter content-type = %q, want text/html", contentType)
	}
	if _, ok := navigation.Request().Headers()["datastar-request"]; ok {
		t.Fatal("native filter navigation must not send the datastar-request header")
	}
	assertMemberFilterQuery(t, nativePage.URL(), search)
	must(t, expect.Locator(memberSearch).ToHaveValue(search))
	memberRows := nativePage.Locator("#access-page tbody tr").Filter(playwright.LocatorFilterOptions{HasText: account.DisplayName})
	must(t, expect.Locator(memberRows).ToHaveCount(1))
}

// assertMemberFilterQuery checks the member filter form serialized every
// field, including the empty role selection.
func assertMemberFilterQuery(t *testing.T, raw, search string) {
	t.Helper()
	parsed, err := url.Parse(raw)
	must(t, err)
	query := parsed.Query()
	if query.Get("tab") != "members" || query.Get("q") != search || !query.Has("role") || query.Get("role") != "" {
		t.Fatalf("member filter query = %q, want tab=members, q=%q and an empty role", parsed.RawQuery, search)
	}
}

// deniedAwarePage opens a page that fails the test on browser errors, except
// the console error Chromium logs for the expected 403 document at deniedURL.
func deniedAwarePage(t *testing.T, context playwright.BrowserContext, deniedURL string) playwright.Page {
	t.Helper()
	page, err := context.NewPage()
	must(t, err)
	t.Cleanup(func() { _ = page.Close() })
	var (
		mu     sync.Mutex
		errors []string
	)
	record := func(message string) {
		mu.Lock()
		defer mu.Unlock()
		errors = append(errors, message)
	}
	page.OnConsole(func(message playwright.ConsoleMessage) {
		if message.Type() != "error" {
			return
		}
		location := message.Location()
		if deniedURL != "" && deniedDocumentError.MatchString(message.Text()) && location != nil && location.URL == deniedURL {
			return
		}
		record("console: " + message.Text())
	})
	page.OnPageError(func(err error) { record("pageerror: " + err.Error()) })
	t.Cleanup(func() {
		mu.Lock()
		defer mu.Unlock()
		if len(errors) > 0 {
			t.Errorf("unexpected browser console and uncaught page errors:\n%s", strings.Join(errors, "\n"))
		}
	})
	return page
}

// cloneSession opens a context that shares the cookies of from, optionally
// with JavaScript disabled to exercise the native HTML fallbacks.
func cloneSession(t *testing.T, from playwright.BrowserContext, javaScript bool) playwright.BrowserContext {
	t.Helper()
	state, err := from.StorageState()
	must(t, err)
	context, err := browser.NewContext(playwright.BrowserNewContextOptions{
		BaseURL:           new(baseURL),
		JavaScriptEnabled: new(javaScript),
		StorageState:      state.ToOptionalStorageState(),
	})
	must(t, err)
	t.Cleanup(func() { _ = context.Close() })
	return context
}

// eventually polls condition until it holds or the assertion timeout passes.
func eventually(t *testing.T, what string, condition func() bool) {
	t.Helper()
	deadline := time.Now().Add(10 * time.Second)
	for !condition() {
		if time.Now().After(deadline) {
			t.Fatalf("timed out waiting for %s", what)
		}
		time.Sleep(50 * time.Millisecond)
	}
}

// viewportScreenshot captures one full-page shot at the current viewport when
// DASHBOARD_E2E_SCREENSHOTS names an output directory.
func viewportScreenshot(t *testing.T, page playwright.Page, name string) {
	t.Helper()
	dir := os.Getenv("DASHBOARD_E2E_SCREENSHOTS")
	if dir == "" {
		return
	}
	path := filepath.Join(dir, name+".png")
	must(t, os.MkdirAll(filepath.Dir(path), 0o755))
	_, err := page.Screenshot(playwright.PageScreenshotOptions{Path: new(path), FullPage: new(true)})
	must(t, err)
}
