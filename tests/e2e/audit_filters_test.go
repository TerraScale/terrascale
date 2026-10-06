package e2e

import (
	"net/url"
	"os"
	"strings"
	"testing"

	"github.com/mxschmitt/playwright-go"
)

func TestAuditFiltersAndExportKeepAppliedValues(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	registerAndOnboard(t, page, newAccount("audit-filters"))
	goToOK(t, page, "/settings?tab=audit")
	retention := page.Locator(`form[action="/api/v1/dashboard/settings/audit-retention"]`)
	fill(t, retention.Locator(`[name="retentionDays"]`), "30")
	submitAndExpect(t, page, "/api/v1/dashboard/settings/audit-retention", 303, byRole(retention, *playwright.AriaRoleButton, "Save retention").Click)

	goToOK(t, page, "/audit")
	filters := page.Locator("#recent-activity .ts-audit-filters")
	values := map[string]string{
		"search": "audit_retention", "action": "settings.audit_retention_updated",
		"severity": "info", "range": "24h",
	}
	fill(t, filters.Locator(`[name="search"]`), values["search"])
	for _, name := range []string{"action", "severity", "range"} {
		_, err := filters.Locator(`[name="` + name + `"]`).SelectOption(playwright.SelectOptionValues{Values: new([]string{values[name]})})
		must(t, err)
	}
	click(t, byRole(filters, *playwright.AriaRoleButton, "Apply filters"))
	waitForPath(t, page, func(u *url.URL) bool {
		for name, value := range values {
			if u.Query().Get(name) != value {
				return false
			}
		}
		return u.Path == "/organization" && u.Query().Get("tab") == "audit"
	})
	for name, value := range values {
		must(t, expect.Locator(filters.Locator(`[name="`+name+`"]`)).ToHaveValue(value))
	}
	must(t, expect.Locator(page.Locator("#recent-activity tbody tr")).ToHaveCount(1))
	must(t, expect.Locator(page.Locator("#recent-activity tbody")).ToContainText(values["action"]))

	appliedURL := page.URL()
	for _, format := range []string{"csv", "json"} {
		click(t, filters.Locator(".ts-menu > summary"))
		link := byRole(filters, *playwright.AriaRoleLink, "Export "+strings.ToUpper(format))
		visible(t, link)
		href, err := link.GetAttribute("href")
		must(t, err)
		exportURL, err := url.Parse(href)
		must(t, err)
		if exportURL.Path != "/audit/export" || exportURL.Query().Get("format") != format {
			t.Fatalf("export URL = %q, want /audit/export with format %s", href, format)
		}
		for name, value := range values {
			if exportURL.Query().Get(name) != value {
				t.Errorf("export %s = %q, want %q", name, exportURL.Query().Get(name), value)
			}
		}
		download, err := page.ExpectDownload(func() error { return link.Click() })
		must(t, err)
		path, err := download.Path()
		must(t, err)
		content, err := os.ReadFile(path)
		must(t, err)
		if !strings.HasSuffix(download.SuggestedFilename(), "."+format) || !strings.Contains(string(content), values["action"]) {
			t.Errorf("export %s did not download the filtered audit event", format)
		}
		if page.URL() != appliedURL {
			t.Errorf("export changed the filter page URL to %q", page.URL())
		}
		must(t, page.Keyboard().Press("Escape"))
	}
}
