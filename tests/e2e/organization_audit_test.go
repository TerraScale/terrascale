package e2e

import (
	"net/url"
	"testing"

	"github.com/mxschmitt/playwright-go"
)

func TestOrganizationAuditTabsAndLegacyRedirect(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	login(t, page, sampleAccount.Email, sampleAccount.Password)
	goToOK(t, page, "/audit?search=database&action=all&severity=info&range=30d&page=2&tab=projects")
	landed, err := url.Parse(page.URL())
	must(t, err)
	if landed.Path != "/organization" || landed.Query().Get("tab") != "audit" {
		t.Fatalf("legacy audit did not select Organization Audit: %s", page.URL())
	}
	for name, value := range map[string]string{"search": "database", "action": "all", "severity": "info", "range": "30d", "page": "2"} {
		if landed.Query().Get(name) != value {
			t.Errorf("redirect %s = %q, want %q", name, landed.Query().Get(name), value)
		}
	}
	tabs := pageByRole(page, *playwright.AriaRoleNavigation, "Organization sections")
	must(t, expect.Locator(tabs.Locator("a")).ToHaveText([]string{"Organization", "Members", "Invitations", "Projects", "Billing", "Audit"}))
	must(t, expect.Locator(byRole(tabs, *playwright.AriaRoleLink, "Audit", true)).ToHaveAttribute("aria-current", "page"))
	must(t, expect.Locator(page.Locator("main")).ToHaveCount(1))
	navigation := pageByRole(page, *playwright.AriaRoleNavigation, "Dashboard sections")
	must(t, expect.Locator(navigation.Locator("a[href='/audit']")).ToHaveCount(0))
	must(t, expect.Locator(navigation.Locator("a[href='/organization']")).ToHaveAttribute("aria-current", "page"))
	visible(t, pageByRole(page, *playwright.AriaRoleSearch, "Filter audit events"))
	viewportScreenshot(t, page, "organization-audit-desktop")

	for _, label := range []string{"Organization", "Members", "Invitations", "Projects", "Billing"} {
		click(t, byRole(tabs, *playwright.AriaRoleLink, label, true))
		must(t, expect.Locator(byRole(tabs, *playwright.AriaRoleLink, label, true)).ToHaveAttribute("aria-current", "page"))
		if currentPath(t, page) != "/organization" {
			t.Fatalf("%s left Organization: %s", label, page.URL())
		}
		must(t, expect.Locator(page.Locator("#recent-activity, #audit-breakdown, .ts-audit-filters")).ToHaveCount(0))
	}
	click(t, byRole(tabs, *playwright.AriaRoleLink, "Audit", true))
	visible(t, pageByRole(page, *playwright.AriaRoleHeading, "Recent activity", true))
	_, err = page.Reload()
	must(t, err)
	must(t, expect.Locator(byRole(tabs, *playwright.AriaRoleLink, "Audit", true)).ToHaveAttribute("aria-current", "page"))
	must(t, page.SetViewportSize(390, 844))
	visible(t, byRole(tabs, *playwright.AriaRoleLink, "Audit", true))
	must(t, byRole(tabs, *playwright.AriaRoleLink, "Audit", true).Focus())
	must(t, expect.Locator(byRole(tabs, *playwright.AriaRoleLink, "Audit", true)).ToBeFocused())
	viewportScreenshot(t, page, "organization-audit-mobile")

	goToOK(t, page, "/organization?tab=audit&action=unknown-action")
	must(t, expect.Locator(page.Locator("#audit-action")).ToHaveValue("unknown-action"))
	visible(t, page.GetByText("No audit events match these filters", playwright.PageGetByTextOptions{Exact: new(true)}))
}

func TestOrganizationAuditFiltersWithoutJavaScript(t *testing.T) {
	t.Parallel()
	context, err := browser.NewContext(playwright.BrowserNewContextOptions{
		BaseURL: new(baseURL), JavaScriptEnabled: new(false),
	})
	must(t, err)
	t.Cleanup(func() { _ = context.Close() })
	page := watchedPage(t, context)
	goToOK(t, page, "/audit?range=30d")
	landed, err := url.Parse(page.URL())
	must(t, err)
	if landed.Path != "/login" || landed.Query().Get("returnTo") != "/audit?range=30d" {
		t.Fatalf("anonymous audit access did not require login: %s", page.URL())
	}
	login(t, page, sampleAccount.Email, sampleAccount.Password)
	goToOK(t, page, "/organization?tab=audit")
	filters := pageByRole(page, *playwright.AriaRoleSearch, "Filter audit events")
	fill(t, filters.Locator(`[name="search"]`), "database")
	click(t, byRole(filters, *playwright.AriaRoleButton, "Apply filters"))
	waitForPath(t, page, func(u *url.URL) bool {
		return u.Path == "/organization" && u.Query().Get("tab") == "audit" && u.Query().Get("search") == "database"
	})
	visible(t, pageByRole(page, *playwright.AriaRoleHeading, "Recent activity", true))
}
