package ui

import (
	"html"
	"strings"
	"testing"

	"github.com/a-h/templ"
	"terrascale.tech/dashboard/internal/core"
)

func TestShellOpensNotificationsFromTheAccountMenu(t *testing.T) {
	for unread, label := range map[int]string{0: "Notifications", 3: "Notifications, 3 unread", 150: "Notifications, 99+ unread"} {
		page := core.Page{Title: "Users", Shell: core.Shell{UnreadNotifications: unread}}
		var out strings.Builder
		if err := Shell(page, templ.NopComponent).Render(t.Context(), &out); err != nil {
			t.Fatal(err)
		}
		markup := html.UnescapeString(out.String())
		// The sidebar no longer links the inbox.
		if strings.Contains(markup, `href="/notifications"`) {
			t.Errorf("%d unread: the shell still links /notifications", unread)
		}
		// The account menu opens the notifications dialog instead, named by its
		// unread count.
		item := `<button id="ts-notifications-menu" class="ts-menu-item" type="button" data-tone="amber" aria-label="` + label + `" command="show-modal" commandfor="ts-notifications-dialog"`
		if !strings.Contains(markup, item) {
			t.Errorf("%d unread: the account menu lacks %s", unread, item)
		}
		// The top-bar bell opens the same dialog.
		bell := `<button id="ts-notifications-topbar" class="ts-icon-btn ts-topbar-notifications" type="button" data-tone="amber" aria-label="` + label + `" command="show-modal" commandfor="ts-notifications-dialog"`
		if !strings.Contains(markup, bell) {
			t.Errorf("%d unread: the top bar lacks %s", unread, bell)
		}
		if pill := `<span class="ts-nav-count" aria-hidden="true">` + countText(unread) + `</span>`; unread > 0 && !strings.Contains(markup, pill) {
			t.Errorf("%d unread: the notifications entries lack the %s count", unread, countText(unread))
		}
		// The shell carries the dialog the entries open.
		if !strings.Contains(markup, `<dialog id="ts-notifications-dialog"`) {
			t.Errorf("%d unread: the shell lacks the notifications dialog", unread)
		}
	}
}

func TestSidebarScrollHandoffIsScopedToDashboardNavigation(t *testing.T) {
	children := core.AuthNavigation()
	children[len(children)-1].Active = true
	page := core.Page{Shell: core.Shell{Navigation: []core.NavGroup{{Items: []core.NavItem{
		{Label: "Authentication", URL: "/auth", Active: true, Children: children},
	}}}}}
	for _, admin := range []bool{false, true} {
		var out strings.Builder
		if err := shellSidebar(page, admin).Render(t.Context(), &out); err != nil {
			t.Fatal(err)
		}
		markup := html.UnescapeString(out.String())
		for _, attr := range []string{`data-init="` + navScrollRestore + `"`, `data-on:click__window="` + navScrollSave + `"`} {
			if strings.Contains(markup, attr) == admin {
				t.Errorf("admin=%t: unexpected sidebar scroll attribute %s", admin, attr)
			}
		}
		for _, want := range []string{
			`<details class="ts-nav-collapsible" data-tone="emerald" open>`,
			`href="/auth/branding" aria-current="page"`,
		} {
			if !strings.Contains(markup, want) {
				t.Errorf("admin=%t: missing native navigation markup %s", admin, want)
			}
		}
		footer := markup[strings.Index(markup, `<div class="ts-sidebar-footer">`):]
		if strings.Contains(footer, navScrollKey) {
			t.Error("sidebar footer must not hand off scroll")
		}
	}
}

func TestNavigationCountsAreNamedAsTheirPillShowsThem(t *testing.T) {
	for _, tc := range []struct {
		item core.NavItem
		want string
	}{
		{core.NavItem{Label: "Organization", BadgeCount: 1, BadgeLabel: CountLabel(1, "organization", "organizations")}, "Organization, 1 organization"},
		{core.NavItem{Label: "Organization", BadgeCount: 4, BadgeLabel: CountLabel(4, "organization", "organizations")}, "Organization, 4 organizations"},
		{notificationsItem(120), "Notifications, 99+ unread"},
		{notificationsItem(0), "Notifications"},
		{core.NavItem{Label: "Databases", BadgeCount: 2}, "Databases"},
	} {
		if got := navLabel(tc.item); got != tc.want {
			t.Errorf("navLabel(%+v) = %q, want %q", tc.item, got, tc.want)
		}
	}
}
