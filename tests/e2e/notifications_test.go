package e2e

import (
	"fmt"
	"net/url"
	"regexp"
	"testing"

	"github.com/mxschmitt/playwright-go"
)

// TestNotificationsUnreadCountAndReadFlow follows the seeded account's unread
// notifications from the sidebar count to marking one read on the Unread tab.
func TestNotificationsUnreadCountAndReadFlow(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	login(t, page, sampleAccount.Email, sampleAccount.Password)

	goToOK(t, page, "/notifications?status=unread")
	navigation := pageByRole(page, *playwright.AriaRoleNavigation, "Dashboard sections")
	link := byRole(navigation, *playwright.AriaRoleLink, "Notifications")
	must(t, expect.Locator(link).ToHaveAttribute("aria-current", "page"))
	items := page.Locator(".ts-notification-list > li")
	unread, err := items.Count()
	must(t, err)
	expectUnreadCount(t, link, unread)

	account := page.Locator("details.ts-account")
	click(t, account.Locator("summary"))
	visible(t, byRole(account, *playwright.AriaRoleLink, "Profile"))
	for _, label := range []string{"Notifications", "Settings"} {
		must(t, expect.Locator(byRole(account, *playwright.AriaRoleLink, label)).ToHaveCount(0))
	}
	must(t, page.Keyboard().Press("Escape"))

	if unread > 0 {
		click(t, byRole(items.First(), *playwright.AriaRoleButton, "Mark read"))
		waitForPath(t, page, func(u *url.URL) bool {
			return u.Path == "/notifications" && u.Query().Get("status") == "unread"
		})
		must(t, expect.Locator(items).ToHaveCount(unread-1))
		expectUnreadCount(t, link, unread-1)
	}

	goToOK(t, page, "/notifications")
	must(t, expect.Locator(pageByRole(page, *playwright.AriaRoleLink, "API key expiring soon")).ToHaveAttribute("href", "/api-keys"))
	must(t, expect.Locator(pageByRole(page, *playwright.AriaRoleLink, "Backup completed")).ToHaveAttribute("href", regexp.MustCompile(`^/databases/db_\w+/backups$`)))
}

// expectUnreadCount checks the Notifications link announces unread, or no
// count when everything is read.
func expectUnreadCount(t *testing.T, link playwright.Locator, unread int) {
	t.Helper()
	if unread == 0 {
		must(t, expect.Locator(link).ToHaveAccessibleName("Notifications"))
		return
	}
	must(t, expect.Locator(link).ToHaveAccessibleName(fmt.Sprintf("Notifications, %d unread", unread)))
	must(t, expect.Locator(link.Locator(".ts-nav-count")).ToHaveText(fmt.Sprint(unread)))
}
