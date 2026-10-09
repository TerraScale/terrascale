package e2e

import (
	"net/http"
	"net/http/httptest"
	"strings"
	"sync/atomic"
	"testing"

	"github.com/mxschmitt/playwright-go"
)

// codeEditor waits for the Monaco editor that upgrades the textarea with id.
// Every browser context downloads Monaco again, which takes longer than the
// default assertion timeout on a busy CI runner.
func codeEditor(t *testing.T, page playwright.Page, id string) playwright.Locator {
	t.Helper()
	editor := page.Locator(`ts-code-editor[data-ready]:has(> textarea#` + id + `)`)
	must(t, expect.Locator(editor.Locator(".monaco-editor .view-lines")).ToBeVisible(playwright.LocatorAssertionsToBeVisibleOptions{Timeout: new(30_000.0)}))
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
	collection := identifier("e2e_editor")
	goToOK(t, page, databasePath+"/documents?collection="+collection)

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
	fill(t, dialog.Locator("#new-document-json"), `{ "id": "customer-7", "ID": 42, "customer_id": "cus_e2e" }`)
	must(t, expect.Locator(editor.Locator(".squiggly-error")).ToHaveCount(0))
	submitAndExpect(t, page, "/api/v1/dashboard/data/documents", 303, byRole(dialog, *playwright.AriaRoleButton, "Add document").Click)
	must(t, expect.Locator(page.Locator("#documents table")).ToContainText(id))
	must(t, expect.Locator(page.Locator("#documents thead th")).ToHaveText([]string{"id", "payload.id", "payload.ID", "customer_id", "Actions"}))
	must(t, expect.Locator(page.Locator("#documents tbody tr").First()).ToContainText("customer-7"))
	viewportScreenshot(t, page, "ts754-payload-grid")
	goToOK(t, page, databasePath+"/sql?collection="+collection)
	query := page.Locator(`form[action="/api/v1/dashboard/data/sql"]`)
	submitAndExpect(t, page, "/api/v1/dashboard/data/sql", 200, byRole(query, *playwright.AriaRoleButton, "Run query").Click)
	result := page.Locator("#sql-result")
	must(t, expect.Locator(result.Locator("thead th")).ToHaveText([]string{"id", "payload.id", "payload.ID", "customer_id"}))
	must(t, expect.Locator(result.Locator("tbody tr").First().Locator("td")).ToHaveText([]string{id, "customer-7", "42", "cus_e2e"}))
	viewportScreenshot(t, page, "ts754-payload-sql")
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

func TestCodeEditorNeverFetchesRemoteJSONSchemas(t *testing.T) {
	t.Parallel()
	var hits atomic.Int32
	remote := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		hits.Add(1)
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write([]byte(`{"type":"object"}`))
	}))
	t.Cleanup(remote.Close)

	page := newPage(t)
	login(t, page, sampleAccount.Email, sampleAccount.Password)
	goToOK(t, page, "/databases")
	databasePath := linkPath(t, page, pageByRole(page, *playwright.AriaRoleLink, "core-primary"))
	goToOK(t, page, databasePath+"/documents?collection="+uniqueName("e2e_editor"))
	click(t, pageByRole(page, *playwright.AriaRoleButton, "Add document"))
	dialog := pageByRole(page, *playwright.AriaRoleDialog, "Add document")
	editor := codeEditor(t, page, "new-document-json")

	// The language service reports the schema it may not load; by then it has
	// already decided whether to fetch it.
	fill(t, dialog.Locator("#new-document-json"), `{ "$schema": "`+remote.URL+`/schema.json", "customer_id": "cus_e2e" }`)
	visible(t, editor.Locator(".squiggly-warning").First())
	if n := hits.Load(); n != 0 {
		t.Fatalf("the editor fetched a remote JSON schema %d time(s)", n)
	}

	scripts, err := page.Request().Get("/js/code-editor.js")
	must(t, err)
	if policy := scripts.Headers()["content-security-policy"]; policy != "default-src 'none'; script-src 'self'" {
		t.Fatalf("editor scripts and workers need their own CSP, got %q", policy)
	}
}

func TestCodeEditorRequiredValidationFocusesEditor(t *testing.T) {
	t.Parallel()
	page := newPage(t)
	login(t, page, sampleAccount.Email, sampleAccount.Password)
	goToOK(t, page, "/databases")
	databasePath := linkPath(t, page, pageByRole(page, *playwright.AriaRoleLink, "core-primary"))
	goToOK(t, page, databasePath+"/sql?collection=orders")

	click(t, pageByRole(page, *playwright.AriaRoleButton, "Save query"))
	dialog := pageByRole(page, *playwright.AriaRoleDialog, "Save query")
	codeEditor(t, page, "saved-sql")
	fill(t, dialog.Locator("#query-name"), uniqueName("e2e-query"))
	fill(t, dialog.Locator("#saved-sql"), "")
	click(t, byRole(dialog, *playwright.AriaRoleButton, "Save query"))

	focused, err := page.Evaluate(`() => !!document.activeElement?.closest('ts-code-editor .monaco-editor')`)
	must(t, err)
	if focused != true {
		t.Fatal("an empty required editor must take focus when the form is submitted")
	}
	must(t, expect.Locator(page.Locator("#saved-sql")).ToHaveAttribute("aria-hidden", "true"))
}
