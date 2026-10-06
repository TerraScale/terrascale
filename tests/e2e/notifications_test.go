package e2e

import (
	"fmt"
	"net/url"
	"strconv"
	"strings"
	"testing"

	"github.com/mxschmitt/playwright-go"
)

// TestNotificationsOpenInTheProfileDialog follows the seeded account's unread
// notifications from the account menu to the in-place dialog: the sidebar no
// longer links the inbox, opening it never navigates, and marking one read
// updates the list and the unread counts without a reload.
func TestNotificationsOpenInTheProfileDialog(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	login(t, page, sampleAccount.Email, sampleAccount.Password)
	goToOK(t, page, "/")

	// The sidebar no longer carries the inbox.
	navigation := pageByRole(page, *playwright.AriaRoleNavigation, "Dashboard sections")
	must(t, expect.Locator(navigation.GetByRole(*playwright.AriaRoleLink, playwright.LocatorGetByRoleOptions{Name: "Notifications"})).ToHaveCount(0))

	menuItem := page.Locator("#ts-notifications-menu")
	unread := 0
	if count, err := menuItem.Locator(".ts-nav-count").TextContent(); err == nil {
		unread, err = strconv.Atoi(strings.TrimSpace(count))
		must(t, err)
	}

	account := page.Locator("details.ts-account")
	click(t, account.Locator("summary"))
	click(t, menuItem)

	// The dialog opens over the current page, which stays put.
	dialog := page.Locator("#ts-notifications-dialog")
	must(t, expect.Locator(dialog).ToHaveAttribute("open", ""))
	items := dialog.Locator(".ts-notification-list > li")
	must(t, expect.Locator(items.First()).ToBeVisible())
	waitForPath(t, page, func(u *url.URL) bool { return u.Path == "/" })
	must(t, expect.Locator(dialog).ToContainText(fmt.Sprintf("%d unread", unread)))

	if unread > 0 {
		click(t, byRole(items.First(), *playwright.AriaRoleButton, "Mark read"))
		// The patch refreshes the inbox, the summary, and both entry points'
		// counts without a navigation.
		if unread > 1 {
			must(t, expect.Locator(menuItem.Locator(".ts-nav-count")).ToHaveText(fmt.Sprint(unread-1)))
		} else {
			must(t, expect.Locator(menuItem.Locator(".ts-nav-count")).ToHaveCount(0))
		}
		must(t, expect.Locator(dialog).ToContainText(fmt.Sprintf("%d unread", unread-1)))
	}

	// The unread tab filters the dialog in place.
	click(t, byRole(dialog, *playwright.AriaRoleButton, "Unread"))
	remaining := unread
	if unread > 0 {
		remaining--
	}
	if remaining > 0 {
		must(t, expect.Locator(items).ToHaveCount(remaining))
	} else {
		must(t, expect.Locator(dialog).ToContainText("You're all caught up"))
	}
	waitForPath(t, page, func(u *url.URL) bool { return u.Path == "/" })

	// Closing lands back on the page that was open underneath.
	must(t, page.Keyboard().Press("Escape"))
	must(t, expect.Locator(dialog).Not().ToHaveAttribute("open", ""))
	waitForPath(t, page, func(u *url.URL) bool { return u.Path == "/" })
}
