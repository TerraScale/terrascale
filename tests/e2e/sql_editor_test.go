package e2e

import (
	"net/url"
	"strings"
	"testing"

	"github.com/mxschmitt/playwright-go"
)

// TestSQLEditorCompletesTheCatalog types into the SQL console and checks what
// the completion widget, the diagnostics, the formatter and the run history
// offer, from the database's collections, their schema and sampled fields,
// and the TerraBase keywords.
func TestSQLEditorCompletesTheCatalog(t *testing.T) {
	t.Parallel()
	// The test fails the SQL check on purpose, which Chromium logs.
	page := watchedPageExcept(t, newContext(t, baseURL), func(message playwright.ConsoleMessage) bool {
		location := message.Location()
		return location != nil && strings.HasSuffix(location.URL, "/api/v1/dashboard/data/sql/check") && strings.Contains(message.Text(), "status of 503")
	})
	login(t, page, sampleAccount.Email, sampleAccount.Password)
	goToOK(t, page, "/databases")
	databasePath := linkPath(t, page, pageByRole(page, *playwright.AriaRoleLink, "core-primary"))

	// A collection of its own, without a schema, has its fields sampled.
	collection := uniqueName("e2e_people")
	goToOK(t, page, databasePath+"/documents?collection="+collection)
	click(t, pageByRole(page, *playwright.AriaRoleButton, "Add document"))
	dialog := pageByRole(page, *playwright.AriaRoleDialog, "Add document")
	codeEditor(t, page, "new-document-json")
	fill(t, dialog.Locator("#new-document-json"), `{ "name": "Ada", "address": { "city": "London" } }`)
	submitAndExpect(t, page, "/api/v1/dashboard/data/documents", 303, byRole(dialog, *playwright.AriaRoleButton, "Add document").Click)

	// A name that is not one SQL name completes quoted: here its -- would
	// otherwise start a comment.
	spaced := uniqueName("e2e--notes")
	goToOK(t, page, databasePath+"/documents?collection="+url.QueryEscape(spaced))
	click(t, pageByRole(page, *playwright.AriaRoleButton, "Add document"))
	dialog = pageByRole(page, *playwright.AriaRoleDialog, "Add document")
	codeEditor(t, page, "new-document-json")
	fill(t, dialog.Locator("#new-document-json"), `{ "title": "Notes" }`)
	submitAndExpect(t, page, "/api/v1/dashboard/data/documents", 303, byRole(dialog, *playwright.AriaRoleButton, "Add document").Click)

	goToOK(t, page, databasePath+"/sql?collection=orders")
	editor := codeEditor(t, page, "sql")
	suggestions := editor.Locator(".suggest-widget .monaco-list")
	replace := func(text string) {
		t.Helper()
		// Typing needs focus on Monaco's input, independent of rendered-line layout.
		input := byRole(editor, *playwright.AriaRoleTextbox, "SQL. Press Ctrl+M to toggle Tab moving focus.", true)
		must(t, input.Focus())
		must(t, expect.Locator(input).ToBeFocused())
		must(t, page.Keyboard().Press("Escape"))
		must(t, page.Keyboard().Press("ControlOrMeta+A"))
		must(t, page.Keyboard().Type(text))
	}

	// FROM completes collections.
	replace("SELECT * FROM ")
	must(t, expect.Locator(suggestions).ToContainText(collection))
	must(t, expect.Locator(suggestions).ToContainText("orders"))

	// Fields of the FROM collection come from its schema, with their types.
	replace("SELECT * FROM orders WHERE tot")
	must(t, expect.Locator(suggestions.Locator(".monaco-list-row").First()).ToContainText("total"))
	must(t, expect.Locator(suggestions.Locator(".monaco-list-row").First()).ToContainText("float"))

	// A dotted path completes the members of the object it names.
	replace("SELECT * FROM " + collection + " WHERE address.")
	must(t, expect.Locator(suggestions.Locator(".monaco-list-row").First()).ToContainText("city"))
	must(t, expect.Locator(suggestions.Locator(".monaco-list-row").First()).ToContainText("string"))

	// Completing inside a name replaces all of it.
	replace("SELECT * FROM orders WHERE total")
	must(t, page.Keyboard().Press("ArrowLeft"))
	must(t, page.Keyboard().Press("ArrowLeft"))
	must(t, page.Keyboard().Press("Control+Space"))
	must(t, expect.Locator(suggestions.Locator(".monaco-list-row").First()).ToContainText("total"))
	must(t, page.Keyboard().Press("Enter"))
	must(t, expect.Locator(page.Locator("#sql")).ToHaveValue("SELECT * FROM orders WHERE total"))

	replace("SELECT * FROM " + spaced[len(spaced)-12:])
	must(t, expect.Locator(suggestions.Locator(".monaco-list-row").First()).ToContainText(spaced))
	must(t, page.Keyboard().Press("Enter"))
	must(t, expect.Locator(page.Locator("#sql")).ToHaveValue(`SELECT * FROM "`+spaced+`"`))
	must(t, page.Keyboard().Type(" WHERE ti"))
	must(t, expect.Locator(suggestions.Locator(".monaco-list-row").First()).ToContainText("title"))

	// Keywords are the TerraBase dialect's.
	replace("SELECT * FROM orders WH")
	must(t, expect.Locator(suggestions).ToContainText("WHERE"))

	// A field named from is a field: the FROM clause still names the
	// collection whose fields complete.
	replace("SELECT from FROM orders WHERE tot")
	must(t, expect.Locator(suggestions.Locator(".monaco-list-row").First()).ToContainText("total"))

	// The server flags what TerraBase rejects, in place.
	replace("SELECT lower(status) FROM orders")
	visible(t, editor.Locator(".squiggly-warning").First())

	click(t, pageByRole(page, *playwright.AriaRoleButton, "Format"))
	must(t, expect.Locator(page.Locator("#sql")).ToHaveValue("SELECT lower(status)\nFROM orders"))

	// Format keeps comments as written and ends a line comment's line.
	fill(t, page.Locator("#sql"), "select * from orders -- keep  -- this\nwhere total > 1")
	click(t, pageByRole(page, *playwright.AriaRoleButton, "Format"))
	must(t, expect.Locator(page.Locator("#sql")).ToHaveValue("select *\nfrom orders -- keep  -- this\nwhere total > 1"))

	// Hyphenated collection names complete and format whole, and a
	// parameter, which the console cannot bind, stays whole and is an error.
	replace("select * from " + collection[:len(collection)-4])
	must(t, expect.Locator(suggestions).ToContainText(collection))
	replace("select * from " + collection + " where total > $1 and total < 1e3 or total > 1e-3")
	visible(t, editor.Locator(".squiggly-error").First())
	click(t, pageByRole(page, *playwright.AriaRoleButton, "Format"))
	must(t, expect.Locator(page.Locator("#sql")).ToHaveValue("select *\nfrom "+collection+"\nwhere total > $1\n  and total < 1e3\n  or total > 1e-3"))

	// A check that cannot run says so rather than leaving the SQL unmarked.
	must(t, page.Route("**/api/v1/dashboard/data/sql/check", func(route playwright.Route) {
		_ = route.Fulfill(playwright.RouteFulfillOptions{Status: new(503), Body: "service unavailable"})
	}))
	replace("SELECT * FROM orders LIMIT 5")
	visible(t, editor.Locator(".squiggly-warning").First())
	must(t, page.Unroute("**/api/v1/dashboard/data/sql/check"))

	replace("select * from orders limit 2")
	must(t, page.Keyboard().Press("Escape"))
	submitAndExpect(t, page, "/api/v1/dashboard/data/sql", 200, func(...playwright.LocatorClickOptions) error {
		return page.Keyboard().Press("ControlOrMeta+Enter")
	})
	codeEditor(t, page, "sql")
	history := page.Locator("select[data-sql-history]")
	must(t, expect.Locator(history.Locator("option")).ToHaveText([]string{"Recent queries", "select * from orders limit 2"}))

	// The history lives in the page and its form only: the next login
	// starts empty and no statement stays in the browser's storage.
	logout(t, page)
	login(t, page, sampleAccount.Email, sampleAccount.Password)
	goToOK(t, page, databasePath+"/sql?collection=orders")
	codeEditor(t, page, "sql")
	must(t, expect.Locator(history).ToBeHidden())
	stored, err := page.Evaluate(`() => [...Object.keys(sessionStorage), ...Object.keys(localStorage)].filter(key => key.includes('sql')).length`)
	must(t, err)
	if stored != 0 {
		t.Fatalf("%v SQL history entries outlived the login", stored)
	}
}
