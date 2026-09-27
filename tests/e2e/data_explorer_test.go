package e2e

import (
	"net/url"
	"regexp"
	"strings"
	"testing"

	"github.com/mxschmitt/playwright-go"
)

// sampleDatabase signs in to the seeded account and returns the path of its
// core-primary database.
func sampleDatabase(t *testing.T, page playwright.Page) string {
	t.Helper()
	login(t, page, sampleAccount.Email, sampleAccount.Password)
	goToOK(t, page, "/databases")
	return linkPath(t, page, pageByRole(page, *playwright.AriaRoleLink, "core-primary"))
}

// identifier is a unique collection name that SQL and schemas accept.
func identifier(prefix string) string {
	return strings.ReplaceAll(uniqueName(prefix), "-", "_")
}

func TestDataSQLConsoleShowsResultsAsTableAndFailuresInPlace(t *testing.T) {
	t.Parallel()
	page := formErrorPage(t)
	databasePath := sampleDatabase(t, page)

	goToOK(t, page, databasePath+"/sql?collection=orders")
	run := func(status int) {
		t.Helper()
		query := page.Locator(`form[action="/api/v1/dashboard/data/sql"]`)
		submitAndExpect(t, page, "/api/v1/dashboard/data/sql", status, byRole(query, *playwright.AriaRoleButton, "Run query").Click)
	}
	run(200)
	result := page.Locator("#sql-result")
	must(t, expect.Locator(result).ToContainText("3 rows"))
	must(t, expect.Locator(result.Locator("thead th")).ToHaveText([]string{"id", "customerId", "createdAt", "status", "total"}))
	rows := result.Locator("tbody tr")
	must(t, expect.Locator(rows).ToHaveCount(3))
	must(t, expect.Locator(rows.First().Locator("td")).ToHaveText([]string{"ord_1001", "cus_1001", "2026-09-17T07:02:00Z", "paid", "725.5"}))
	click(t, result.GetByText("Raw JSON"))
	must(t, expect.Locator(result.Locator("pre")).ToContainText(`"id": "ord_1001"`))

	// The seeded saved query needs a WHERE the demo cannot execute: the
	// console says so and keeps the statement.
	click(t, pageByRole(page, *playwright.AriaRoleLink, "orders-over-500"))
	codeEditor(t, page, "sql")
	run(400)
	failure := page.Locator("main").GetByRole(*playwright.AriaRoleAlert)
	must(t, expect.Locator(failure).ToContainText("Query failed"))
	must(t, expect.Locator(failure).ToContainText("SELECT * FROM collection [LIMIT n]"))
	must(t, expect.Locator(page.Locator("#sql")).ToHaveValue("SELECT * FROM orders WHERE total > 500"))
	must(t, expect.Locator(result).ToHaveCount(0))

	goToOK(t, page, databasePath+"/sql?collection="+identifier("e2e_empty"))
	run(200)
	visible(t, result.GetByText("No rows matched."))
}

func TestDataSavedQueryErrorsReopenTheDialogAndDeletesAreConfirmed(t *testing.T) {
	t.Parallel()
	page := formErrorPage(t)
	databasePath := sampleDatabase(t, page)
	goToOK(t, page, databasePath+"/sql?collection=orders")

	save := func(name, sql string, status int) {
		t.Helper()
		click(t, pageByRole(page, *playwright.AriaRoleButton, "Save query"))
		dialog := pageByRole(page, *playwright.AriaRoleDialog, "Save query")
		codeEditor(t, page, "saved-sql")
		fill(t, dialog.Locator("#query-name"), name)
		fill(t, dialog.Locator("#saved-sql"), sql)
		submitAndExpect(t, page, "/api/v1/dashboard/data/saved-queries", status, byRole(dialog, *playwright.AriaRoleButton, "Save query").Click)
	}
	name := uniqueName("e2e-query")
	save(name, "DELETE FROM orders", 400)
	dialog := pageByRole(page, *playwright.AriaRoleDialog, "Save query")
	must(t, expect.Locator(dialog.GetByRole(*playwright.AriaRoleAlert)).ToContainText("Only SELECT, WITH, and EXPLAIN statements are allowed."))
	must(t, expect.Locator(dialog.Locator("#query-name")).ToHaveValue(name))
	must(t, expect.Locator(dialog.Locator("#saved-sql")).ToHaveValue("DELETE FROM orders"))
	must(t, page.Keyboard().Press("Escape"))

	save(name, "SELECT * FROM orders LIMIT 5", 303)
	saved := page.Locator(".ts-data-queries > li").Filter(playwright.LocatorFilterOptions{HasText: name})
	click(t, byRole(saved, *playwright.AriaRoleButton, "Delete "+name))
	confirm := pageByRole(page, *playwright.AriaRoleDialog, "Delete "+name+"?")
	visible(t, confirm)
	click(t, byRole(confirm, *playwright.AriaRoleButton, "Cancel"))
	must(t, expect.Locator(confirm).ToBeHidden())
	must(t, expect.Locator(saved).ToHaveCount(1))

	click(t, byRole(saved, *playwright.AriaRoleButton, "Delete "+name))
	submitAndExpect(t, page, "/api/v1/dashboard/data/saved-queries/delete", 303, byRole(confirm, *playwright.AriaRoleButton, "Delete query").Click)
	must(t, expect.Locator(saved).ToHaveCount(0))
}

func TestDataDocumentFormsKeepInputOnErrorAndConfirmDeletes(t *testing.T) {
	t.Parallel()
	page := formErrorPage(t)
	databasePath := sampleDatabase(t, page)
	collection := uniqueName("e2e_docs")
	documentsPath := databasePath + "/documents?collection=" + collection
	goToOK(t, page, documentsPath)

	add := func(id, body string, status int) {
		t.Helper()
		click(t, pageByRole(page, *playwright.AriaRoleButton, "Add document"))
		dialog := pageByRole(page, *playwright.AriaRoleDialog, "Add document")
		codeEditor(t, page, "new-document-json")
		fill(t, dialog.Locator("#new-document-id"), id)
		fill(t, dialog.Locator("#new-document-json"), body)
		submitAndExpect(t, page, "/api/v1/dashboard/data/documents", status, byRole(dialog, *playwright.AriaRoleButton, "Add document").Click)
	}
	id := uniqueName("doc")
	add(id, `{"total": 1}`, 303)
	table := page.Locator("table.ts-data-documents")
	must(t, expect.Locator(table).ToContainText(id))

	add(id, `{"total": 2}`, 409)
	dialog := pageByRole(page, *playwright.AriaRoleDialog, "Add document")
	must(t, expect.Locator(dialog.GetByRole(*playwright.AriaRoleAlert)).ToContainText("A document with ID "+id+" already exists"))
	must(t, expect.Locator(dialog.Locator("#new-document-id")).ToHaveValue(id))
	must(t, expect.Locator(dialog.Locator("#new-document-json")).ToHaveValue(`{"total": 2}`))
	must(t, page.Keyboard().Press("Escape"))
	must(t, expect.Locator(dialog).ToBeHidden())

	goToOK(t, page, documentsPath+"&edit="+id)
	editor := pageByRole(page, *playwright.AriaRoleDialog, id)
	codeEditor(t, page, "document-json")
	selectOption(t, editor.Locator("#operation"), "patch")
	fill(t, editor.Locator("#document-json"), `[1]`)
	submitAndExpect(t, page, "/api/v1/dashboard/data/documents", 400, byRole(editor, *playwright.AriaRoleButton, "Save document").Click)
	must(t, expect.Locator(editor.GetByRole(*playwright.AriaRoleAlert)).ToContainText("A patch must be a JSON object"))
	must(t, expect.Locator(editor.Locator("#operation")).ToHaveValue("patch"))
	must(t, expect.Locator(editor.Locator("#document-json")).ToHaveValue("[1]"))
	must(t, page.Keyboard().Press("Escape"))

	row := table.Locator("tbody tr").Filter(playwright.LocatorFilterOptions{HasText: id})
	click(t, row.Locator(`details.ts-menu > summary[aria-label="Actions for `+id+`"]`))
	click(t, byRole(row, *playwright.AriaRoleLink, "Delete"))
	confirm := pageByRole(page, *playwright.AriaRoleDialog, "Delete "+id+"?")
	visible(t, confirm)
	must(t, expect.Locator(table).ToContainText(id))
	submitAndExpect(t, page, "/api/v1/dashboard/data/documents", 303, byRole(confirm, *playwright.AriaRoleButton, "Delete document").Click)
	visible(t, page.GetByText("No documents match this collection and filter."))
}

func TestDataSchemaStartsValidAndExplainsRejectedSchemas(t *testing.T) {
	t.Parallel()
	page := formErrorPage(t)
	databasePath := sampleDatabase(t, page)
	apply := func(status int) {
		t.Helper()
		form := page.Locator(`form[action="/api/v1/dashboard/schema"]`)
		submitAndExpect(t, page, "/api/v1/dashboard/schema", status, byRole(form, *playwright.AriaRoleButton, "Apply schema").Click)
	}

	// The seeded schema is shown indented and applies unchanged.
	goToOK(t, page, databasePath+"/schema?collection=orders")
	must(t, expect.Locator(page.Locator("#schema-json")).ToHaveValue(regexp.MustCompile(`\n  "name": "orders",`)))
	apply(303)
	waitForPath(t, page, func(u *url.URL) bool { return u.Path == databasePath+"/schema" })

	// A collection without a schema starts from a valid one.
	collection := identifier("e2e_schema")
	goToOK(t, page, databasePath+"/schema?collection="+collection)
	must(t, expect.Locator(page.Locator("#schema-json")).ToHaveValue(regexp.MustCompile(`"name": "`+collection+`"`)))
	codeEditor(t, page, "schema-json")
	fill(t, page.Locator("#schema-json"), `{"type": "object"}`)
	apply(400)
	must(t, expect.Locator(page.Locator("main").GetByRole(*playwright.AriaRoleAlert)).ToContainText(`The schema "name" must be "`+collection+`"`))
	must(t, expect.Locator(page.Locator("#schema-json")).ToHaveValue(`{"type": "object"}`))

	goToOK(t, page, databasePath+"/schema?collection="+collection)
	apply(303)
}
