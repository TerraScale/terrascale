package e2e

import (
	"fmt"
	"net/url"
	"testing"

	"github.com/mxschmitt/playwright-go"
)

func TestAPIKeyFiltersInListHeader(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	login(t, page, sampleAccount.Email, sampleAccount.Password)
	for _, width := range []int{360, 390, 768, 960, 980, 1024, 1440, 1920} {
		must(t, page.SetViewportSize(width, 900))
		for _, kind := range []string{"", "key", "m2m"} {
			t.Run(fmt.Sprintf("%s/%d", kind, width), func(t *testing.T) {
				goToOK(t, page, "/api-keys?kind="+kind)
				must(t, expect.Locator(page.Locator(".ts-toolbar form, .ts-toolbar input, .ts-toolbar select, .ts-toolbar button")).ToHaveCount(0))
				must(t, expect.Locator(page.Locator(".ts-toolbar .ts-tab")).ToHaveCount(3))
				form := page.Locator("#credentials .ts-card-head .ts-apikeys-filters")
				must(t, expect.Locator(form).ToHaveCount(1))
				must(t, expect.Locator(form.Locator(`[name="kind"]`)).ToHaveValue(kind))
				assertAPIKeyHeaderLayout(t, page)
				if width == 390 || width == 1440 {
					viewportScreenshot(t, page, fmt.Sprintf("api-keys/header-%s-%d", kind, width))
				}
				fill(t, form.Locator(`[name="q"]`), "core")
				selectOption(t, form.Locator(`[name="status"]`), "active")
				selectOption(t, form.Locator(`[name="databaseId"]`), "db_01JC9Q8Y5N7P9R3T5V7X9Z2AC")
				must(t, expect.Locator(form.Locator(`[name="databaseId"]`)).ToHaveAttribute("title", "core-primary"))
				click(t, byRole(form, *playwright.AriaRoleButton, "Filter"))
				waitForPath(t, page, func(u *url.URL) bool {
					q := u.Query()
					return u.Path == "/api-keys" && q.Get("kind") == kind && q.Get("q") == "core" &&
						q.Get("status") == "active" && q.Get("databaseId") == "db_01JC9Q8Y5N7P9R3T5V7X9Z2AC"
				})
				must(t, expect.Locator(form.Locator(`[name="q"]`)).ToHaveValue("core"))
				must(t, expect.Locator(form.Locator(`[name="status"]`)).ToHaveValue("active"))
				must(t, expect.Locator(form.Locator(`[name="databaseId"]`)).ToHaveValue("db_01JC9Q8Y5N7P9R3T5V7X9Z2AC"))
				must(t, expect.Locator(form.Locator(`[name="databaseId"]`)).ToHaveAttribute("title", "core-primary"))
				assertAPIKeyHeaderLayout(t, page)
			})
		}
	}
}

func assertAPIKeyHeaderLayout(t *testing.T, page playwright.Page) {
	t.Helper()
	raw, err := page.Evaluate(`() => {
		const header = document.querySelector('#credentials .ts-card-head');
		const actions = header.querySelector('.ts-card-actions');
		const form = actions.querySelector('form');
		const controls = [...form.querySelectorAll('input:not([type="hidden"]), select, button')];
		const create = actions.querySelector(':scope > button');
		const problems = [];
		if (controls.map(el => el.name || el.textContent.trim()).join(',') !== 'q,status,databaseId,Filter')
			problems.push('filter control order changed');
		if (form.nextElementSibling !== create)
			problems.push('create action must follow the filter form');
		if (document.documentElement.scrollWidth > innerWidth + 1)
			problems.push('horizontal page overflow');
		const bounds = header.getBoundingClientRect();
		const boxes = [...controls, ...form.querySelectorAll('a'), create].map(el => el.getBoundingClientRect());
		if (innerWidth >= 1440 && [240, 160, 160].some((width, i) => Math.abs(boxes[i].width - width) > 1))
			problems.push('desktop filter field widths changed');
		for (const box of boxes) {
			if (box.width <= 0 || box.left < bounds.left - 1 || box.right > bounds.right + 1)
				problems.push('control escapes the list header');
		}
		for (let i = 0; i < boxes.length; i++) {
			for (let j = i + 1; j < boxes.length; j++) {
				const a = boxes[i], b = boxes[j];
				if (Math.min(a.right, b.right) > Math.max(a.left, b.left) + 1 &&
					Math.min(a.bottom, b.bottom) > Math.max(a.top, b.top) + 1)
					problems.push('controls overlap');
				if (Math.abs(a.top + a.height / 2 - b.top - b.height / 2) > 1 &&
					Math.min(a.bottom, b.bottom) > Math.max(a.top, b.top) + 1)
					problems.push('controls on the same row are not vertically centered');
			}
		}
		if (Math.abs(create.getBoundingClientRect().right - actions.getBoundingClientRect().right) > 1)
			problems.push('create action is not at the right edge');
		return problems.join('\n');
	}`)
	must(t, err)
	if raw != "" {
		t.Fatal(raw)
	}
}

// TestAPIKeySecretsAreShownOnceAndChangesAreConfirmed covers the credential
// lifecycle: invalid input that keeps what was typed, a secret that a reload
// cannot reissue, and confirmed rotation and revocation that keep the list's
// filters.
func TestAPIKeySecretsAreShownOnceAndChangesAreConfirmed(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	registerAndOnboard(t, page, newAccount("api-keys"))
	keyName := uniqueName("deploy")
	goToOK(t, page, "/api-keys")
	must(t, expect.Locator(page.Locator(".ts-page-actions button")).ToHaveCount(0))
	must(t, expect.Locator(page.Locator("#credentials .ts-card-actions > button")).ToHaveCount(1))
	must(t, expect.Locator(byRole(page.Locator(".ts-page"), *playwright.AriaRoleButton, "Create machine credential")).ToHaveCount(0))
	goToOK(t, page, "/api-keys?kind=m2m")
	click(t, byRole(page.Locator("#credentials .ts-card-actions"), *playwright.AriaRoleButton, "Create machine credential"))
	visible(t, page.Locator("dialog#create-m2m"))
	goToOK(t, page, "/api-keys")
	click(t, byRole(page.Locator("#credentials .ts-card-actions"), *playwright.AriaRoleButton, "Create key"))
	dialog := page.Locator("dialog#create-key")
	visible(t, dialog)
	create := dialog.Locator(`form[action="/api/v1/dashboard/credentials/key"]`)
	fill(t, create.Locator(`[name="name"]`), keyName)
	fill(t, create.Locator(`[name="scopes"]`), ",,,")
	submitAndExpect(t, page, "/api/v1/dashboard/credentials/key", 303, byRole(create, *playwright.AriaRoleButton, "Create key").Click)
	waitForPath(t, page, func(u *url.URL) bool { return u.Path == "/api-keys" && u.Query().Get("create") == "key" })
	visible(t, dialog)
	must(t, expect.Locator(dialog.GetByRole(*playwright.AriaRoleAlert)).ToContainText("1 to 16 scopes"))
	must(t, expect.Locator(create.Locator(`[name="name"]`)).ToHaveValue(keyName))
	must(t, expect.Locator(create.Locator(`[name="scopes"]`)).ToHaveValue(",,,"))

	fill(t, create.Locator(`[name="scopes"]`), "database:read")
	submitAndExpect(t, page, "/api/v1/dashboard/credentials/key", 200, byRole(create, *playwright.AriaRoleButton, "Create key").Click)
	secret := page.Locator(".ts-apikeys-secret")
	must(t, expect.Locator(secret.GetByRole(*playwright.AriaRoleHeading)).ToHaveText("Secret for "+keyName))
	waitForPath(t, page, func(u *url.URL) bool { return u.Path == "/api-keys" && u.RawQuery == "" })
	first, err := secret.Locator("code").TextContent()
	must(t, err)

	_, err = page.Reload()
	must(t, err)
	row := page.Locator("#credentials tbody tr").Filter(playwright.LocatorFilterOptions{HasText: keyName})
	must(t, expect.Locator(row).ToHaveCount(1))
	must(t, expect.Locator(secret).ToHaveCount(0))

	// Rotating and revoking from a filtered list keep its filters.
	goToOK(t, page, "/api-keys?kind=key")
	must(t, expect.Locator(row).ToHaveCount(1))

	click(t, byRole(row, *playwright.AriaRoleButton, "Rotate"))
	confirm := page.Locator("dialog[open]")
	visible(t, byRole(confirm, *playwright.AriaRoleHeading, "Rotate "+keyName+"?"))
	submitAndExpect(t, page, "/api/v1/dashboard/credentials/rotate", 200, byRole(confirm, *playwright.AriaRoleButton, "Rotate secret").Click)
	must(t, expect.Locator(secret.GetByRole(*playwright.AriaRoleHeading)).ToHaveText("New secret for "+keyName))
	must(t, expect.Locator(secret.Locator("code")).Not().ToHaveText(first))
	waitForPath(t, page, func(u *url.URL) bool { return u.Path == "/api-keys" && u.RawQuery == "kind=key" })

	click(t, byRole(row, *playwright.AriaRoleButton, "Revoke"))
	visible(t, byRole(confirm, *playwright.AriaRoleHeading, "Revoke "+keyName+"?"))
	submitAndExpect(t, page, "/api/v1/dashboard/credentials/revoke", 303, byRole(confirm, *playwright.AriaRoleButton, "Revoke credential").Click)
	waitForPath(t, page, func(u *url.URL) bool { return u.Path == "/api-keys" && u.RawQuery == "kind=key" })
	must(t, expect.Locator(row).ToContainText("Revoked"))
	must(t, expect.Locator(byRole(row, *playwright.AriaRoleButton, "Rotate")).ToHaveCount(0))
}
