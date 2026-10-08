package data

import (
	"bytes"
	"context"
	"fmt"
	"math/big"
	"strings"
	"testing"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/features/databases"
)

// render returns the HTML of one data section.
func render(t *testing.T, d pageData) string {
	t.Helper()
	d.Database = core.Database{ID: "19T9WF6BRDZ1HTM3X1C9G1V4YJ", Name: "primary", Status: "ready"}
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
	const message = "A document with ID 209AR8XRJWZZQ2627T9V0T3JST already exists in orders."
	page := render(t, pageData{Section: "documents", CanWrite: true, FormError: message, Dialog: "add-document", NewDocumentID: "209AR8XRJWZZQ2627T9V0T3JST", NewDocument: `{"a":1}`})
	dialog := page[strings.Index(page, `<dialog id="add-document"`):]
	dialog = dialog[:strings.Index(dialog, "</dialog>")]
	for _, want := range []string{message, `data-init="el.showModal()"`, `value="209AR8XRJWZZQ2627T9V0T3JST"`, `{&#34;a&#34;:1}`} {
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

func TestDocumentsListEveryCollectionWithDeepLinks(t *testing.T) {
	page := render(t, pageData{Section: "documents", CanWrite: true, Collections: []collectionEntry{{Name: "orders", Count: "3"}, {Name: "users", Count: "0"}}, Documents: []documentView{{ID: "209AR8XRJWZZQ2627T9V0T3JST", Value: []byte(`{}`)}}})
	for _, want := range []string{
		`<nav class="ts-data-nav" aria-label="Collections"`,
		`href="/databases/19T9WF6BRDZ1HTM3X1C9G1V4YJ/documents/orders" aria-current="page"`,
		`href="/databases/19T9WF6BRDZ1HTM3X1C9G1V4YJ/documents/users">`,
		`href="/databases/19T9WF6BRDZ1HTM3X1C9G1V4YJ/documents/orders?edit=209AR8XRJWZZQ2627T9V0T3JST"`,
		`<option value="orders" selected>orders (3)</option>`,
		`commandfor="new-collection"`,
		`action="/api/v1/dashboard/data/collections"`,
	} {
		if !strings.Contains(page, want) {
			t.Errorf("the documents page lacks %q", want)
		}
	}
	if strings.Contains(page, "Filter collections") {
		t.Error("a short list offers a filter")
	}

	many := make([]collectionEntry, 9)
	for i := range many {
		many[i].Name = fmt.Sprintf("c%d", i)
	}
	if page := render(t, pageData{Section: "documents", Collections: many}); !strings.Contains(page, `data-bind="collectionSearch"`) || strings.Contains(page, "new-collection") {
		t.Error("a long list offers a filter, and a reader is not offered a new collection")
	}
}

func TestDocumentsOfferToCreateTheFirstCollection(t *testing.T) {
	page := render(t, pageData{Section: "documents", CanWrite: true})
	if !strings.Contains(page, "This database has no collections yet.") || strings.Contains(page, `id="documents"`) || strings.Contains(page, "collection-select") {
		t.Error("an empty database shows the empty state instead of documents")
	}
	if !strings.Contains(page, ">New collection</span>") {
		t.Error("the empty state offers to create a collection")
	}
	failed := render(t, pageData{Section: "documents", CanWrite: true, Dialog: "new-collection", NewCollection: "orders", FormError: "A collection named orders already exists."})
	dialog := failed[strings.Index(failed, `<dialog id="new-collection"`):]
	dialog = dialog[:strings.Index(dialog, "</dialog>")]
	for _, want := range []string{"A collection named orders already exists.", `value="orders"`, `data-init="el.showModal()"`} {
		if !strings.Contains(dialog, want) {
			t.Errorf("the reopened new-collection dialog lacks %q", want)
		}
	}
}

func TestSortDocumentsByFieldThenID(t *testing.T) {
	docs := []documentView{
		{ID: "d", Value: []byte(`{"total":10}`)},
		{ID: "c", Value: []byte(`{"total":9}`)},
		{ID: "b", Value: []byte(`{"total":"high"}`)},
		{ID: "a", Value: []byte(`{}`)},
		{ID: "e", Value: []byte(`{"total":9}`)},
	}
	ids := func() string {
		var out []string
		for _, d := range docs {
			out = append(out, d.ID)
		}
		return strings.Join(out, ",")
	}
	for _, tc := range []struct {
		order documentSort
		want  string
	}{
		{documentSort{}, "a,b,c,d,e"},
		{documentSort{Desc: true}, "e,d,c,b,a"},
		// Missing first, then numbers by value, then strings.
		{documentSort{Field: "total"}, "a,c,e,d,b"},
		{documentSort{Field: "total", Desc: true}, "b,d,e,c,a"},
	} {
		sortDocuments(docs, tc.order)
		if got := ids(); got != tc.want {
			t.Errorf("sort %+v = %s, want %s", tc.order, got, tc.want)
		}
	}
}

func TestDocumentsPaginateAndSortFromTheGrid(t *testing.T) {
	data := pageData{Layout: "grid", Page: 9, Order: documentSort{Field: "total", Desc: true}}
	for i := range 30 {
		data.Documents = append(data.Documents, documentView{ID: fmt.Sprintf("ord_%02d", i), Value: []byte(fmt.Sprintf(`{"total":%d,"status":"paid"}`, i))})
	}
	paginate(&data)
	if data.Page != 2 || data.PageCount != 2 || data.Matched != 30 || len(data.Documents) != 5 || data.Documents[0].ID != "ord_25" {
		t.Fatalf("page 9 of 30 documents = page %d of %d with %d rows from %s", data.Page, data.PageCount, len(data.Documents), data.Documents[0].ID)
	}
	data.Section, data.Collections = "documents", []collectionEntry{{Name: "orders"}}
	page := render(t, data)
	for _, want := range []string{
		`<th scope="col" class="ts-num" aria-sort="descending"><a class="ts-data-sort" href="/databases/19T9WF6BRDZ1HTM3X1C9G1V4YJ/documents/orders?sort=total">`,
		`<a class="ts-data-sort" href="/databases/19T9WF6BRDZ1HTM3X1C9G1V4YJ/documents/orders?sort=status">`,
		`href="/databases/19T9WF6BRDZ1HTM3X1C9G1V4YJ/documents/orders?order=desc&amp;page=2&amp;sort=total&amp;view=list"`,
		`rel="prev" href="/databases/19T9WF6BRDZ1HTM3X1C9G1V4YJ/documents/orders?order=desc&amp;page=1&amp;sort=total"`,
		"26–30 of 30 documents · page 2 of 2",
		`data-kind="number"`,
	} {
		if !strings.Contains(page, want) {
			t.Errorf("the grid lacks %q", want)
		}
	}
	if strings.Contains(page, `rel="next"`) {
		t.Error("the last page links to a next page")
	}
}

func TestCollectionNamesStayAddressable(t *testing.T) {
	for _, name := range []string{".", ".."} {
		if validCollection(name) {
			t.Errorf("%q would be cleaned out of its documents link", name)
		}
	}
	for name, want := range map[string]bool{"orders": true, "e2e_docs-2": true, "Orders": false, "..": false, "2fa": false, "a/b": false} {
		if collectionName.MatchString(name) != want {
			t.Errorf("new collection %q accepted = %v, want %v", name, !want, want)
		}
	}
}

func TestSortDocumentsComparesNumbersExactly(t *testing.T) {
	two600 := new(big.Int).Lsh(big.NewInt(1), 600)
	ascending := []string{
		"-1e400", "-2", "-0.5", "0", "-0.0", "0.3", "0.30000000000000000001", "99", "1e2", "100.5",
		"9007199254740992", "9007199254740993", two600.String(), new(big.Int).Add(two600, big.NewInt(1)).String(), "1E700",
	}
	var docs []documentView
	for i := len(ascending) - 1; i >= 0; i-- {
		docs = append(docs, documentView{ID: fmt.Sprintf("d%02d", i), Value: []byte(`{"n":` + ascending[i] + `}`)})
	}
	sortDocuments(docs, documentSort{Field: "n"})
	for i, doc := range docs {
		// 0 and -0.0 are equal and fall back to ID order.
		if want := fmt.Sprintf("d%02d", i); doc.ID != want {
			t.Fatalf("position %d holds %s (%s), want %s", i, doc.ID, doc.Value, want)
		}
	}
}

func TestGridDoesNotSortTheWholeValueColumn(t *testing.T) {
	data := pageData{Section: "documents", Layout: "grid", Collections: []collectionEntry{{Name: "orders"}}, More: true,
		Documents: []documentView{{ID: "a", Value: []byte(`20`)}, {ID: "b", Value: []byte(`{"total":3}`)}}}
	paginate(&data)
	page := render(t, data)
	if strings.Contains(page, "?sort=value") || !strings.Contains(page, "?sort=total") {
		t.Error("only real fields sort; the value column holds whole non-object documents")
	}
	data.Documents, data.Matched = nil, 0
	if page := render(t, data); !strings.Contains(page, "more than 1000 documents") {
		t.Error("a truncated view says so even when nothing matches")
	}
}

func TestGridSortsOnlyAddressableFields(t *testing.T) {
	long := strings.Repeat("f", maxSortField+1)
	data := pageData{Section: "documents", Layout: "grid", Collections: []collectionEntry{{Name: "orders"}},
		Documents: []documentView{{ID: "a", Value: []byte(`7`)}, {ID: "b", Value: []byte(`{"value":42,"":1,"` + long + `":2}`)}}}
	paginate(&data)
	if len(data.Grid.Columns) != 4 || data.Grid.Columns[0].Root != true || data.Grid.Columns[1].Root {
		t.Fatalf("columns = %+v, want the whole value apart from the value field", data.Grid.Columns)
	}
	page := render(t, data)
	if !strings.Contains(page, "?sort=value") || strings.Count(page, "?sort=value") != 1 {
		t.Error("the value field sorts and the whole-value column does not")
	}
	if strings.Contains(page, "sort="+long) || strings.Contains(page, `href="/databases/19T9WF6BRDZ1HTM3X1C9G1V4YJ/documents/orders?sort="`) {
		t.Error("empty and overlong field names are not offered as sorts")
	}
}

func TestDocumentFormsCarryTheBrowsingState(t *testing.T) {
	page := render(t, pageData{Section: "documents", CanWrite: true, Layout: "list", Order: documentSort{Field: "total", Desc: true}, Search: "paid", Page: 3,
		Collections: []collectionEntry{{Name: "orders"}}, Documents: []documentView{{ID: "209AR8XRJWZZQ2627T9V0T3JST", Value: []byte(`{}`)}}, Matched: 1, PageCount: 3})
	form := page[strings.Index(page, `<dialog id="add-document"`):]
	form = form[:strings.Index(form, "</form>")]
	for _, want := range []string{`name="view" value="list"`, `name="sort" value="total"`, `name="order" value="desc"`, `name="q" value="paid"`, `name="page" value="3"`} {
		if !strings.Contains(form, want) {
			t.Errorf("the add-document form lacks %q", want)
		}
	}
}
