package data

import (
	"bytes"
	"context"
	"strings"
	"testing"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/features/databases"
)

// render returns the HTML of one data section.
func render(t *testing.T, d pageData) string {
	t.Helper()
	d.Database = core.Database{ID: "db_1", Name: "primary", Status: "ready"}
	d.Collection = "orders"
	var out bytes.Buffer
	frame := databases.FrameProps{Database: d.Database, Tab: sections[d.Section].Tab}
	if err := sectionPage(core.Page{CSRFToken: "token"}, frame, d).Render(context.Background(), &out); err != nil {
		t.Fatal(err)
	}
	return out.String()
}

func TestSchemaAndIndexChangesAreOfferedOnlyToManagers(t *testing.T) {
	for _, tc := range []struct {
		section, control string
	}{
		{"schema", "Apply schema"},
		{"indexes", "Create index"},
	} {
		member := render(t, pageData{Section: tc.section, IndexView: "overview", CanWrite: true, Schema: starterSchema("orders")})
		if strings.Contains(member, tc.control) {
			t.Errorf("%s: a member is offered %q, which the server refuses", tc.section, tc.control)
		}
		manager := render(t, pageData{Section: tc.section, IndexView: "overview", CanWrite: true, CanManage: true, Schema: starterSchema("orders")})
		if !strings.Contains(manager, tc.control) {
			t.Errorf("%s: a manager is not offered %q", tc.section, tc.control)
		}
	}
	if member := render(t, pageData{Section: "schema", CanWrite: true, Schema: "{}"}); !strings.Contains(member, "readonly") {
		t.Error("a member sees the schema read-only")
	}
}

func TestFormErrorsShowBesideTheirInput(t *testing.T) {
	const message = "A document with ID ord_1 already exists in orders."
	page := render(t, pageData{Section: "documents", CanWrite: true, FormError: message, Dialog: "add-document", NewDocumentID: "ord_1", NewDocument: `{"a":1}`})
	dialog := page[strings.Index(page, `<dialog id="add-document"`):]
	dialog = dialog[:strings.Index(dialog, "</dialog>")]
	for _, want := range []string{message, `data-init="el.showModal()"`, `value="ord_1"`, `{&#34;a&#34;:1}`} {
		if !strings.Contains(dialog, want) {
			t.Errorf("the reopened add-document dialog lacks %q", want)
		}
	}
	if strings.Count(page, message) != 1 {
		t.Error("a dialog error is shown once, inside the dialog")
	}

	sql := render(t, pageData{Section: "sql", CanWrite: true, SQL: "SELECT * FROM orders WHERE total > 1", FormError: "The in-memory demo runs SELECT * FROM collection [LIMIT n] only."})
	if !strings.Contains(sql, "Query failed") || strings.Contains(sql, `id="sql-result"`) {
		t.Error("a failed run shows the error in place of a result")
	}

	// The statement check would repeat an invalid statement's error or call a
	// rejected one accepted; a failed run shows only its own error.
	for _, analysis := range []sqlAnalysis{{OK: true}, {Error: "Only read-only statements run here."}} {
		sql := render(t, pageData{Section: "sql", CanWrite: true, SQL: "SELECT 1", FormError: "Only read-only statements run here.", SQLAnalysis: analysis})
		if strings.Count(sql, "Only read-only statements run here.") != 1 || strings.Contains(sql, "Read-only statement accepted") {
			t.Errorf("failed run with analysis %+v shows the statement check too", analysis)
		}
	}
}
