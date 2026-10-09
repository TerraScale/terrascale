package e2e

import (
	"net/url"
	"testing"

	"github.com/mxschmitt/playwright-go"
)

func TestAPIKeyPermissionSelection(t *testing.T) {
	page := newPage(t)
	login(t, page, sampleAccount.Email, sampleAccount.Password)
	goToOK(t, page, "/api-keys?create=key")
	dialog := page.Locator("dialog#create-key")
	visible(t, dialog)
	read := byRole(dialog, *playwright.AriaRoleRadio, "Read only")
	write := byRole(dialog, *playwright.AriaRoleRadio, "Read and write")
	review := dialog.Locator(".ts-permission-review")
	must(t, expect.Locator(read).ToBeChecked())
	must(t, expect.Locator(write).Not().ToBeChecked())
	must(t, expect.Locator(review).ToContainText("Cannot create, update, or delete"))
	for _, width := range []int{360, 390, 540, 768, 1440} {
		height := 900
		if width < 768 {
			height = 844
		}
		must(t, page.SetViewportSize(width, height))
		layout, err := dialog.Evaluate(`dialog => {
   const bounds = dialog.getBoundingClientRect();
			const footer = dialog.querySelector('button[type="submit"]').getBoundingClientRect();
			return footer.top >= bounds.top && footer.bottom <= bounds.bottom &&
				bounds.left >= 0 && bounds.right <= innerWidth && dialog.scrollWidth <= dialog.clientWidth + 1 &&
			[...dialog.querySelectorAll('.ts-permission-option')].filter(el => el.getClientRects().length > 0).every(el => {
     const box = el.getBoundingClientRect();
     return box.width >= 40 && box.left >= bounds.left && box.right <= bounds.right;
    });
  }`, nil)
		must(t, err)
		if layout != true {
			t.Fatalf("permission dialog overflows at %dpx", width)
		}
	}
	screenshot(t, page, "api-key-read-only")
	// Native radio navigation must also update the live authorization summary.
	must(t, read.Focus())
	must(t, read.Press("ArrowRight"))
	must(t, expect.Locator(write).ToBeChecked())
	must(t, expect.Locator(review).ToContainText("Can read, create, update, and delete"))
	// Each dialog has its own signals: choosing write in one cannot grant it in another.
	must(t, expect.Locator(page.Locator("#m2m-access-read")).ToBeChecked())
	databaseID := "0Z793APJ2ECFW2F7P6J9F2RKVY"
	selectOption(t, dialog.Locator(`[name="databaseId"]`), databaseID)
	must(t, expect.Locator(review.Locator(".ts-strong")).ToHaveText("core-primary"))
	keyName := uniqueName("permission-write")
	fill(t, dialog.Locator(`[name="name"]`), keyName)
	screenshot(t, page, "api-key-read-write")
	submitAndExpect(t, page, "/api/v1/dashboard/credentials/key", 200, byRole(dialog, *playwright.AriaRoleButton, "Create key").Click)
	row := page.Locator("#credentials tbody tr").Filter(playwright.LocatorFilterOptions{HasText: keyName})
	must(t, expect.Locator(row.Locator(".ts-apikeys-scopes > *")).ToHaveCount(2))
	must(t, expect.Locator(row).ToContainText("database:read"))
	must(t, expect.Locator(row).ToContainText("database:write"))
	must(t, expect.Locator(row).ToContainText("core-primary"))
	// Submitting the default grants only read access, for both credential kinds.
	for _, kind := range []string{"key", "m2m"} {
		goToOK(t, page, "/api-keys?create="+kind)
		dialog := page.Locator("dialog#create-" + kind)
		name := uniqueName("permission-read")
		fill(t, dialog.Locator(`[name="name"]`), name)
		action := "Create key"
		if kind == "m2m" {
			action = "Create client"
		}
		submitAndExpect(t, page, "/api/v1/dashboard/credentials/"+kind, 200, byRole(dialog, *playwright.AriaRoleButton, action).Click)
		row := page.Locator("#credentials tbody tr").Filter(playwright.LocatorFilterOptions{HasText: name})
		must(t, expect.Locator(row.Locator(".ts-apikeys-scopes > *")).ToHaveCount(1))
		must(t, expect.Locator(row).ToContainText("database:read"))
		must(t, expect.Locator(row).Not().ToContainText("database:write"))
	}
	// Ingestion clients can keep write access without acquiring read access.
	for _, kind := range []string{"key", "m2m"} {
		goToOK(t, page, "/api-keys?create="+kind)
		dialog := page.Locator("dialog#create-" + kind)
		click(t, dialog.Locator(".ts-permission-advanced > summary"))
		must(t, byRole(dialog, *playwright.AriaRoleRadio, "Write only").Check())
		must(t, expect.Locator(dialog.Locator(".ts-permission-review")).ToContainText("Cannot read or query"))
		action := "Create key"
		if kind == "m2m" {
			action = "Create client"
		}
		fill(t, dialog.Locator(`[name="name"]`), "   ")
		submitAndExpect(t, page, "/api/v1/dashboard/credentials/"+kind, 303, byRole(dialog, *playwright.AriaRoleButton, action).Click)
		waitForPath(t, page, func(u *url.URL) bool { return u.Query().Get("create") == kind && u.Query().Get("error") != "" })
		must(t, expect.Locator(dialog.Locator(".ts-permission-advanced")).ToHaveAttribute("open", ""))
		must(t, expect.Locator(byRole(dialog, *playwright.AriaRoleRadio, "Write only")).ToBeChecked())
		name := uniqueName("permission-ingest")
		fill(t, dialog.Locator(`[name="name"]`), name)
		submitAndExpect(t, page, "/api/v1/dashboard/credentials/"+kind, 200, byRole(dialog, *playwright.AriaRoleButton, action).Click)
		row := page.Locator("#credentials tbody tr").Filter(playwright.LocatorFilterOptions{HasText: name})
		must(t, expect.Locator(row.Locator(".ts-apikeys-scopes > *")).ToHaveCount(1))
		must(t, expect.Locator(row).ToContainText("database:write"))
		must(t, expect.Locator(row).Not().ToContainText("database:read"))
		if kind == "key" {
			must(t, expect.Locator(row).ToContainText("Write-only key"))
		}
	}

}

func TestAPIKeyRejectedPermissionsRequireANewChoice(t *testing.T) {
	page := newPage(t)
	login(t, page, sampleAccount.Email, sampleAccount.Password)
	for _, scopes := range []string{"", "*", "database:admin"} {
		name := scopes
		if name == "" {
			name = "empty"
		}
		t.Run(name, func(t *testing.T) {
			goToOK(t, page, "/api-keys?create=key")
			dialog := page.Locator("dialog#create-key")
			visible(t, dialog)
			// Submit through the dashboard so its trusted error redirect keeps the draft.
			// An invalid name makes even legacy permissions reopen instead of issuing a key.
			fill(t, dialog.Locator(`[name="name"]`), "   ")
			_, err := dialog.Locator("#key-access-read").Evaluate(`(el, scopes) => el.value = scopes`, scopes)
			must(t, err)
			submitAndExpect(t, page, "/api/v1/dashboard/credentials/key", 303, byRole(dialog, *playwright.AriaRoleButton, "Create key").Click)
			waitForPath(t, page, func(u *url.URL) bool { return u.Query().Get("create") == "key" && u.Query().Get("error") != "" })
			visible(t, dialog)
			must(t, expect.Locator(dialog.Locator(`[name="scopes"]:checked`)).ToHaveCount(0))
			must(t, expect.Locator(dialog.Locator(".ts-permission-review")).ToContainText("Choose an access level"))
			valid, err := dialog.Locator("form").Evaluate(`form => form.checkValidity()`, nil)
			must(t, err)
			if valid != false {
				t.Errorf("rejected scopes %q must require an explicit new choice", scopes)
			}
		})
	}
}
