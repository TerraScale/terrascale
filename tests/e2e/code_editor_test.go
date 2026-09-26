package e2e

import (
	"strings"
	"testing"

	"github.com/mxschmitt/playwright-go"
)

// codeEditor waits for the Monaco editor that upgrades the textarea with id.
func codeEditor(t *testing.T, page playwright.Page, id string) playwright.Locator {
	t.Helper()
	editor := page.Locator(`ts-code-editor[data-ready]:has(> textarea#` + id + `)`)
	visible(t, editor.Locator(".monaco-editor .view-lines"))
	return editor
}

func TestCodeEditorRunsSQLTypedInMonaco(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	login(t, page, sampleAccount.Email, sampleAccount.Password)
	goToOK(t, page, "/databases")
	databasePath := linkPath(t, page, pageByRole(page, *playwright.AriaRoleLink, "core-primary"))

	response := goTo(t, page, databasePath+"/sql?collection=orders")
	policy := response.Headers()["content-security-policy"]
	if !strings.Contains(policy, "style-src-attr 'unsafe-inline'") || strings.Contains(policy, "https://esm.sh") {
		t.Fatalf("SQL console CSP must admit the editor's styles and nothing remote: %s", policy)
	}
	editor := codeEditor(t, page, "sql")
	must(t, expect.Locator(editor.Locator(".view-lines")).ToContainText("SELECT * FROM orders LIMIT 20"))
	must(t, expect.Locator(editor.Locator(".view-lines")).ToHaveCSS("font-feature-settings", `"calt", "liga"`))

	click(t, editor.Locator(".view-lines"))
	must(t, page.Keyboard().Press("ControlOrMeta+A"))
	must(t, page.Keyboard().Type("SELECT * FROM orders LIMIT 2"))
	must(t, page.Keyboard().Press("Escape"))
	must(t, expect.Locator(page.Locator("#sql")).ToHaveValue("SELECT * FROM orders LIMIT 2"))

	submitAndExpect(t, page, "/api/v1/dashboard/data/sql", 200, func(...playwright.LocatorClickOptions) error {
		return page.Keyboard().Press("ControlOrMeta+Enter")
	})
	visible(t, pageByRole(page, *playwright.AriaRoleHeading, "Query result"))
	must(t, expect.Locator(codeEditor(t, page, "sql").Locator(".view-lines")).ToContainText("SELECT * FROM orders LIMIT 2"))
}

func TestCodeEditorValidatesAndSubmitsJSONDocument(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	login(t, page, sampleAccount.Email, sampleAccount.Password)
	goToOK(t, page, "/databases")
	databasePath := linkPath(t, page, pageByRole(page, *playwright.AriaRoleLink, "core-primary"))
	// A collection of its own keeps the shared sample data unchanged for
	// parallel tests.
	goToOK(t, page, databasePath+"/documents?collection="+uniqueName("e2e_editor"))

	click(t, pageByRole(page, *playwright.AriaRoleButton, "Add document"))
	dialog := pageByRole(page, *playwright.AriaRoleDialog, "Add document")
	editor := codeEditor(t, page, "new-document-json")

	// Writing the textarea updates the editor, and the JSON language service
	// flags the invalid document.
	fill(t, dialog.Locator("#new-document-json"), `{ "customer_id": }`)
	must(t, expect.Locator(editor.Locator(".view-lines")).ToContainText(`"customer_id"`))
	visible(t, editor.Locator(".squiggly-error").First())

	id := uniqueName("ord")
	fill(t, dialog.Locator("#new-document-id"), id)
	fill(t, dialog.Locator("#new-document-json"), `{ "customer_id": "cus_e2e" }`)
	must(t, expect.Locator(editor.Locator(".squiggly-error")).ToHaveCount(0))
	submitAndExpect(t, page, "/api/v1/dashboard/data/documents", 303, byRole(dialog, *playwright.AriaRoleButton, "Add document").Click)
	must(t, expect.Locator(page.Locator("table.ts-data-documents")).ToContainText(id))
}

func TestCodeEditorLoadsOnlyWhereUsed(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	login(t, page, sampleAccount.Email, sampleAccount.Password)
	response := goTo(t, page, "/usage")
	if policy := response.Headers()["content-security-policy"]; strings.Contains(policy, "style-src-attr") {
		t.Fatalf("pages without an editor keep the strict style policy: %s", policy)
	}
	must(t, expect.Locator(page.Locator(`script[src^="/js/code-editor.js"]`)).ToHaveCount(0))
}
