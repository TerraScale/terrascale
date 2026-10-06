package e2e

import (
	"fmt"
	"net/url"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"testing"

	"github.com/mxschmitt/playwright-go"
)

// The accessibility suite runs axe-core, pinned as a development dependency
// in package.json, against the WCAG 2.0, 2.1 and 2.2 level A and AA rules on
// the dashboard's pages, dialogs and menus.

// memberAccessibilityPaths are the signed-in account pages of the populated
// sample account.
var memberAccessibilityPaths = []string{
	"/api-keys", "/access", "/access?tab=members", "/access?tab=projects", "/access?tab=invitations",
	"/organization?tab=audit", "/usage", "/usage?tab=billing", "/subscriptions", "/billing",
	"/organization", "/organization?newOrganization=organization",
	"/profile", "/profile?tab=security", "/profile?tab=sessions", "/notifications",
	"/cli/approve", "/agent/approve",
}

// databaseAccessibilityPaths are the sample account's home and database
// pages. {database} stands for the core-primary database page.
var databaseAccessibilityPaths = []string{
	"/", "/?tab=connect", "/databases",
	"{database}", "{database}/documents", "{database}/sql", "{database}/schema",
	"{database}/indexes", "{database}/backups", "{database}/branches", "{database}/import",
}

// authAccessibilityPaths are the sample account's authentication console
// pages.
var authAccessibilityPaths = []string{
	"/auth", "/auth/quickstart", "/auth/users", "/auth/organizations", "/auth/memberships",
	"/auth/invitations", "/auth/domains", "/auth/sign-in", "/auth/connections", "/auth/mfa",
	"/auth/sessions", "/auth/directories", "/auth/clients", "/auth/templates", "/auth/keys",
	"/auth/webhooks", "/auth/audit", "/auth/branding",
}

var adminAccessibilityPaths = []string{
	"/admin/users", "/admin/organizations", "/admin/projects", "/admin/databases",
	"/admin/estate", "/admin/estate?tab=regions", "/admin/estate?tab=hosts",
	"/admin/estate?tab=operations", "/admin/usage", "/admin/plans",
}

var signedOutAccessibilityPaths = []string{"/login", "/onboarding", "/reset-password"}

func TestAccessibilitySignedOutPages(t *testing.T) {
	t.Parallel()
	context := newContext(t, baseURL)
	for _, path := range signedOutAccessibilityPaths {
		t.Run(path, func(t *testing.T) {
			t.Parallel()
			page := watchedPage(t, context)
			goToOK(t, page, path)
			expectAccessible(t, page, path, "")
		})
	}
}

func TestAccessibilityOnboardingAndEmptyStates(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	registerAndOpenPaymentStep(t, page, newAccount("a11y-onboarding"))
	expectAccessible(t, page, "onboarding payment step", "")

	click(t, byRole(onboardingStep(t, page, "payment-method"), *playwright.AriaRoleButton, "Continue on Free"))
	project := onboardingStep(t, page, "project")
	expectAccessible(t, page, "onboarding project step", "")

	click(t, byRole(project, *playwright.AriaRoleButton, "Create project"))
	waitForPath(t, page, func(u *url.URL) bool { return u.Path == "/" })
	for _, path := range []string{"/", "/databases", "/api-keys", "/usage", "/notifications"} {
		goToOK(t, page, path)
		expectAccessible(t, page, "empty "+path, "")
	}
}

func TestAccessibilityMemberPagesDialogsAndMenus(t *testing.T) {
	t.Parallel()
	context := newContext(t, baseURL)
	login(t, watchedPage(t, context), sampleAccount.Email, sampleAccount.Password)

	t.Run("menus", func(t *testing.T) {
		t.Parallel()
		page := watchedPage(t, context)
		goToOK(t, page, "/")
		for _, menu := range []string{"details.ts-context-menu", "details.ts-account"} {
			click(t, page.Locator(menu+" > summary"))
			must(t, expect.Locator(page.Locator(menu)).ToHaveAttribute("open", ""))
			expectAccessible(t, page, menu, menu)
			must(t, page.Keyboard().Press("Escape"))
		}
	})
	t.Run("mobile navigation", func(t *testing.T) {
		t.Parallel()
		page := watchedPage(t, context)
		must(t, page.SetViewportSize(390, 844))
		goToOK(t, page, "/databases")
		expectAccessible(t, page, "mobile /databases", "")
		click(t, page.Locator("#ts-nav-toggle"))
		must(t, expect.Locator(page.Locator("#ts-nav-close")).ToBeFocused())
		expectAccessible(t, page, "mobile navigation drawer", "")
	})
	for _, path := range memberAccessibilityPaths {
		expectPageAndDialogsAccessible(t, context, path)
	}
}

func TestAccessibilityDatabasePagesAndDialogs(t *testing.T) {
	t.Parallel()
	context := newContext(t, baseURL)
	setup := watchedPage(t, context)
	login(t, setup, sampleAccount.Email, sampleAccount.Password)
	goToOK(t, setup, "/databases")
	database := linkPath(t, setup, pageByRole(setup, *playwright.AriaRoleLink, "core-primary"))
	for _, path := range databaseAccessibilityPaths {
		expectPageAndDialogsAccessible(t, context, strings.ReplaceAll(path, "{database}", database))
	}
}

func TestAccessibilityAuthPagesAndDialogs(t *testing.T) {
	t.Parallel()
	context := newContext(t, baseURL)
	login(t, watchedPage(t, context), sampleAccount.Email, sampleAccount.Password)
	for _, path := range authAccessibilityPaths {
		expectPageAndDialogsAccessible(t, context, path)
	}
}

// expectPageAndDialogsAccessible checks path and its dialogs in a parallel
// subtest on a fresh page of the signed-in context.
func expectPageAndDialogsAccessible(t *testing.T, context playwright.BrowserContext, path string) {
	t.Helper()
	t.Run(path, func(t *testing.T) {
		t.Parallel()
		page := watchedPage(t, context)
		goToOK(t, page, path)
		expectAccessible(t, page, path, "")
		expectDialogsAccessible(t, page, path)
	})
}

func TestAccessibilityAdminPages(t *testing.T) {
	t.Parallel()
	context := newContext(t, baseURL)
	setup := watchedPage(t, context)
	login(t, setup, admin.Email, admin.Password)
	// A recorded estate action fills the operations log, whose table scrolls.
	goToOK(t, setup, "/admin/estate?tab=operations")
	form := setup.Locator(`form[action="/api/v1/admin/estate/action"]`)
	fill(t, form.Locator(`[name="targetId"]`), uniqueName("db_a11y"))
	fill(t, form.Locator(`[name="reason"]`), "Accessibility check of the recorded actions log")
	submitAndExpect(t, setup, "/api/v1/admin/estate/action", 303, byRole(form, *playwright.AriaRoleButton, "Record action").Click)
	for _, path := range adminAccessibilityPaths {
		t.Run(path, func(t *testing.T) {
			t.Parallel()
			page := watchedPage(t, context)
			goToOK(t, page, path)
			expectAccessible(t, page, path, "")
			expectDialogsAccessible(t, page, path)
		})
	}
}

// expectDialogsAccessible opens every visible dialog trigger on the page,
// checks the open dialog, and closes it again with Escape. A dialog the page
// opened on load was checked with the page and is closed first.
func expectDialogsAccessible(t *testing.T, page playwright.Page, path string) {
	t.Helper()
	if count, err := page.Locator("dialog[open]").Count(); err == nil && count > 0 {
		must(t, page.Keyboard().Press("Escape"))
		must(t, expect.Locator(page.Locator("dialog[open]")).ToHaveCount(0))
	}
	triggers, err := page.Locator(`button[command="show-modal"]:visible`).All()
	must(t, err)
	opened := map[string]bool{}
	for _, trigger := range triggers {
		id := requiredAttribute(t, trigger, "commandfor", "dialog trigger must name its dialog")
		if opened[id] {
			continue
		}
		opened[id] = true
		dialog := page.Locator("dialog#" + id)
		click(t, trigger)
		must(t, expect.Locator(dialog).ToHaveAttribute("open", ""))
		expectAccessible(t, page, path+" dialog "+id, "dialog#"+id)
		must(t, page.Keyboard().Press("Escape"))
		must(t, expect.Locator(dialog).Not().ToHaveAttribute("open", ""))
	}
}

// axeSource is the axe-core bundle from node_modules, read once per run.
var axeSource = sync.OnceValues(func() (string, error) {
	source, err := os.ReadFile(filepath.Join("..", "..", "node_modules", "axe-core", "axe.min.js"))
	if err != nil {
		return "", fmt.Errorf("run npm ci to install axe-core: %w", err)
	}
	return string(source), nil
})

// axeRun waits for running transitions and finite animations, such as a
// dialog fading in, to settle; then it checks the element matching the
// selector argument, or the whole document, and describes every violation
// with the elements it affects.
const axeRun = `async (selector) => {
	await Promise.all(document.getAnimations()
		.filter((animation) => animation.effect?.getComputedTiming().endTime !== Infinity)
		.map((animation) => animation.finished.catch(() => {})));
	const result = await axe.run(selector ? document.querySelector(selector) : document, {
		runOnly: { type: "tag", values: ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22a", "wcag22aa"] },
		resultTypes: ["violations"],
	});
	return result.violations.map((violation) =>
		violation.id + " (" + violation.impact + "): " + violation.help + "\n" +
		violation.nodes.map((node) => "    " + node.target.join(" ") + "\n      " + node.failureSummary.replaceAll("\n", "\n      ")).join("\n"));
}`

// expectAccessible fails the test with every WCAG A and AA violation axe
// finds in the page, or in the element matching selector when it is set.
// Code editors are checked once Monaco has replaced their textarea.
func expectAccessible(t *testing.T, page playwright.Page, state, selector string) {
	t.Helper()
	must(t, expect.Locator(page.Locator("ts-code-editor:not([data-ready])")).ToHaveCount(0))
	loaded, err := page.Evaluate(`() => typeof window.axe === "object"`)
	must(t, err)
	if loaded != true {
		source, err := axeSource()
		must(t, err)
		_, err = page.Evaluate(source)
		must(t, err)
	}
	result, err := page.Evaluate(axeRun, selector)
	must(t, err)
	violations, _ := result.([]any)
	if len(violations) == 0 {
		return
	}
	lines := make([]string, len(violations))
	for i, violation := range violations {
		lines[i] = fmt.Sprint(violation)
	}
	t.Errorf("%s has %d accessibility violations:\n%s", state, len(violations), strings.Join(lines, "\n"))
}
