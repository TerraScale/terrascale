package e2e

import (
	"net/url"
	"regexp"
	"testing"

	"github.com/mxschmitt/playwright-go"
)

// TestInvitationLifecycle walks invitations through resend, decline, sign-in
// and accept, a wrong account, and member removal, in separate browsers.
func TestInvitationLifecycle(t *testing.T) {
	t.Parallel()
	owner := newPage(t)
	registerAndOnboard(t, owner, newAccount("invitation-owner"))
	guestAccount := newAccount("invitation-guest")
	guest := newPage(t)
	registerAndOnboard(t, guest, guestAccount)
	// A signed-out visitor checks dead links; their expected 404s would trip
	// the console watcher of a watched page.
	visitor, err := newContext(t, baseURL).NewPage()
	must(t, err)

	// The page that reveals a link must still post its own forms.
	replaced := invite(t, owner, guestAccount.Email)
	row := owner.Locator("#team-invitations tbody tr").Filter(playwright.LocatorFilterOptions{HasText: guestAccount.Email})
	resend, err := url.Parse(requiredAttribute(t, row.Locator(`form[action*="/resend"]`), "action", "a pending invitation can be resent"))
	must(t, err)
	submitAndExpect(t, owner, resend.Path, 200, byRole(row, *playwright.AriaRoleButton, "Resend").Click)
	declined := invitationLink(t, owner)
	if declined == replaced {
		t.Fatal("resending an invitation must replace its link")
	}
	if response := goTo(t, visitor, replaced); response.Status() != 404 {
		t.Fatalf("GET replaced invitation = %d, want 404", response.Status())
	}
	visible(t, pageByRole(visitor, *playwright.AriaRoleHeading, "This invitation is no longer valid"))

	goToOK(t, guest, declined)
	submitAndExpect(t, guest, declined+"/respond", 303, pageByRole(guest, *playwright.AriaRoleButton, "Decline").Click)
	waitForPath(t, guest, func(u *url.URL) bool { return u.Path == "/organization" && u.Query().Get("invitation") == "declined" })
	must(t, expect.Locator(guest.GetByRole(*playwright.AriaRoleStatus).First()).ToContainText("Invitation declined"))
	if response := goTo(t, visitor, declined); response.Status() != 404 {
		t.Fatalf("GET declined invitation = %d, want 404", response.Status())
	}

	accepted := invite(t, owner, guestAccount.Email)
	logout(t, guest)
	goToOK(t, guest, accepted)
	click(t, pageByRole(guest, *playwright.AriaRoleLink, "Sign in"))
	form := guest.Locator(`form[action="/login"]`)
	fill(t, form.Locator(`[name="email"]`), guestAccount.Email)
	fill(t, form.Locator(`[name="password"]`), guestAccount.Password)
	click(t, byRole(form, *playwright.AriaRoleButton, "Sign in"))
	waitForPath(t, guest, func(u *url.URL) bool { return u.Path == accepted })
	submitAndExpect(t, guest, accepted+"/respond", 303, pageByRole(guest, *playwright.AriaRoleButton, "Accept invitation").Click)
	waitForPath(t, guest, func(u *url.URL) bool { return u.Path == "/organization" && u.Query().Get("invitation") == "accepted" })
	must(t, expect.Locator(guest.GetByRole(*playwright.AriaRoleStatus).First()).ToContainText("You are now a member of"))

	stranger := invite(t, owner, newAccount("invitation-stranger").Email)
	goToOK(t, guest, stranger)
	must(t, expect.Locator(guest.GetByRole(*playwright.AriaRoleAlert)).ToContainText("you are signed in as "+guestAccount.Email))
	click(t, pageByRole(guest, *playwright.AriaRoleButton, "Sign out and switch account"))
	waitForPath(t, guest, func(u *url.URL) bool { return u.Path == "/login" && u.Query().Get("returnTo") == stranger })

	goToOK(t, owner, "/organization?tab=members")
	member := owner.Locator("#team-members tbody tr").Filter(playwright.LocatorFilterOptions{HasText: guestAccount.Email})
	click(t, byRole(member, *playwright.AriaRoleButton, "Remove access"))
	confirm := owner.Locator("dialog[open]")
	visible(t, byRole(confirm, *playwright.AriaRoleHeading, "Remove "+guestAccount.DisplayName+"?"))
	click(t, byRole(confirm, *playwright.AriaRoleButton, "Remove access"))
	must(t, expect.Locator(member).ToHaveCount(0))
}

// invite creates an invitation from the owner's Invitations tab and returns
// the path of its link.
func invite(t *testing.T, page playwright.Page, email string) string {
	t.Helper()
	goToOK(t, page, "/organization?tab=invitations")
	form := page.Locator("#team-invite form")
	fill(t, form.Locator(`[name="email"]`), email)
	submitAndExpect(t, page, "/api/v1/access/invitations", 200, byRole(form, *playwright.AriaRoleButton, "Create invitation").Click)
	return invitationLink(t, page)
}

// invitationLink reads the one-time link shown after creating or resending
// an invitation, once the page has left the POST address a reload would
// resubmit.
func invitationLink(t *testing.T, page playwright.Page) string {
	t.Helper()
	link := page.Locator("#invitation-link")
	must(t, expect.Locator(link).ToHaveText(regexp.MustCompile(`^https?://[^/]+/invitations/`)))
	waitForPath(t, page, func(u *url.URL) bool { return u.Path == "/organization" && u.Query().Get("tab") == "invitations" })
	return linkPath(t, page, link)
}
