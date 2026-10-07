package e2e

import (
	"net/http"
	"regexp"
	"testing"
)

// TestAuthenticationEmailsEditorPreviewsAndSavesDrafts edits a project's
// emails in the console: the preview follows unsaved drafts in light and
// dark without growing the history, a test email keeps the drafts, and the
// saved wording can be reset. A rejected save keeps every draft.
func TestAuthenticationEmailsEditorPreviewsAndSavesDrafts(t *testing.T) {
	t.Parallel()
	page := formErrorPage(t)
	account := newAccount("auth-emails")
	registerAndOnboard(t, page, account)
	goToOK(t, page, "/auth/emails?template=sign_in_code&locale=pt-BR")
	preview := page.FrameLocator("#email-preview")
	must(t, expect.Locator(preview.Locator("h1")).ToHaveText("Entre no TerraScale"))
	history, err := page.Evaluate(`() => history.length`)
	must(t, err)

	fill(t, page.Locator("#email-brand-name"), "Acme Cloud")
	fill(t, page.Locator("#email-heading"), "Bem-vindo à {{ app_name }}")
	must(t, expect.Locator(preview.Locator("h1")).ToHaveText("Bem-vindo à Acme Cloud"))
	click(t, page.Locator(`label:has(input[name="color_scheme"][value="dark"])`))
	must(t, expect.Locator(preview.Locator("body")).ToHaveClass("dark"))
	click(t, page.Locator(`label:has(input[name="preview_device"][value="mobile"])`))
	must(t, expect.Locator(page.Locator("#email-preview")).ToHaveJSProperty("offsetWidth", 375))
	if after, err := page.Evaluate(`() => history.length`); err != nil || after != history {
		t.Errorf("history length = %v after typing, want %v", after, history)
	}

	click(t, page.Locator(`button:has-text("Send test to")`))
	must(t, expect.Locator(page.Locator("#email-test-status")).ToHaveText("Test email sent to "+account.Email+"."))
	must(t, expect.Locator(page.Locator("#email-brand-name")).ToHaveValue("Acme Cloud"))

	fill(t, page.Locator("#email-subject"), "Código {{ password }}")
	submitAndExpect(t, page, "/api/v1/auth-management/emails", http.StatusUnprocessableEntity, page.Locator(`button[value="save"]`).Click)
	visible(t, page.GetByText("{{ password }} is not a variable of this email."))
	must(t, expect.Locator(page.Locator("#email-brand-name")).ToHaveValue("Acme Cloud"))
	must(t, expect.Locator(page.Locator("#email-heading")).ToHaveValue("Bem-vindo à {{ app_name }}"))
	must(t, expect.Locator(preview.Locator("body")).ToContainText("{{ password }} is not a variable of this email."))

	fill(t, page.Locator("#email-subject"), "Seu código {{ code }}")
	must(t, expect.Locator(preview.Locator("h1")).ToHaveText("Bem-vindo à Acme Cloud"))
	click(t, page.Locator(`button[value="save"]`))
	must(t, page.WaitForURL(regexp.MustCompile(`saved=1`)))
	visible(t, page.GetByText("Changes saved."))
	must(t, expect.Locator(page.Locator("#email-heading")).ToHaveValue("Bem-vindo à {{ app_name }}"))
	visible(t, page.Locator(`a[aria-current="page"]:has-text("Sign-in code") .ts-badge`))
	viewportScreenshot(t, page, "ts583/emails-saved")

	click(t, page.Locator(`button[value="reset"]`))
	must(t, page.WaitForURL(regexp.MustCompile(`saved=1`)))
	must(t, expect.Locator(page.Locator("#email-heading")).ToHaveValue("Entre no {{ app_name }}"))
	must(t, expect.Locator(page.Locator(`button[value="reset"]`)).ToHaveCount(0))
	must(t, expect.Locator(preview.Locator("h1")).ToHaveText("Entre no Acme Cloud"))
}
