package data

import (
	"reflect"
	"strconv"
	"strings"
	"testing"
)

func TestCatalogCollectionPrefersTheSchema(t *testing.T) {
	schema := []byte(`{"name":"orders","fields":[{"name":"total","type":"float"},{"name":"customer.email","type":"string"}]}`)
	got := catalogCollection("orders", 3, false, schema, [][]byte{[]byte(`{"ignored":true}`)})
	want := []sqlCatalogField{{"_id", "string"}, {"_rev", "string"}, {"customer.email", "string"}, {"total", "float"}}
	if got.Source != "schema" || got.Documents != 3 || !reflect.DeepEqual(got.Fields, want) {
		t.Fatalf("schema collection = %+v, want fields %v from the schema", got, want)
	}
}

func TestCatalogCollectionInfersNestedFieldsFromDocuments(t *testing.T) {
	sample := [][]byte{
		[]byte(`{"name":"Ada","address":{"city":"London","geo":{"lat":51.5}},"tags":["a"],"score":1,"bad-key":1}`),
		[]byte(`{"name":"Lin","address":{"city":null},"score":"high","active":true}`),
	}
	got := catalogCollection("users", 2, true, nil, sample)
	want := []sqlCatalogField{
		{"_id", "string"}, {"_rev", "string"}, {"active", "boolean"}, {"address", "object"},
		{"address.city", "string"}, {"address.geo", "object"}, {"address.geo.lat", "number"},
		{"name", "string"}, {"score", "mixed"}, {"tags", "array"},
	}
	if got.Source != "sample" || !got.Approximate || !reflect.DeepEqual(got.Fields, want) {
		t.Fatalf("inferred collection = %+v, want fields %v", got, want)
	}
}

func TestInferFieldsStopsAtTheParserDepth(t *testing.T) {
	types := map[string]string{}
	inferFields([]byte(`{"a":{"b":{"c":{"d":{"e":{"f":{"g":{"h":{"i":1}}}}}}}}}`), "", 0, types)
	if _, ok := types["a.b.c.d.e.f.g.h"]; !ok {
		t.Error("an 8-member path is a field")
	}
	if _, ok := types["a.b.c.d.e.f.g.h.i"]; ok {
		t.Error("a 9-member path is beyond what TerraBase addresses")
	}
}

func TestSQLDiagnosticsLocateProblems(t *testing.T) {
	type mark struct{ severity, text string }
	for _, tc := range []struct {
		sql  string
		want []mark
	}{
		{"SELECT * FROM orders WHERE total > 1 ORDER BY total DESC LIMIT 20", nil},
		{"  ", nil},
		{"SELECT * FROM orders; DELETE FROM orders", []mark{{"error", "SELECT * FROM orders; DELETE FROM orders"}}},
		{"SELECT * INTO copy FROM orders", []mark{{"error", "INTO"}}},
		{"SELECT * FROM orders WHERE x = 'insert' AND y IN (1, 2) UNION SELECT * FROM t", []mark{{"warning", "UNION"}}},
		{"SELECT DISTINCT status FROM orders GROUP BY status", []mark{{"warning", "DISTINCT"}, {"warning", "GROUP"}}},
		{"SELECT lower(name) FROM users LIMIT 5000", []mark{{"warning", "lower"}, {"warning", "5000"}}},
		{"SELECT * FROM o WHERE a = 'é😀' LIMIT 0", []mark{{"warning", "0"}}},
		{"SELECT * FROM o LIMIT -1", []mark{{"warning", "-1"}}},
		{"SELECT * FROM o LIMIT foo", []mark{{"warning", "foo"}}},
		{"SELECT * FROM o LIMIT 1.5", []mark{{"warning", "1.5"}}},
		{"SELECT * FROM o LIMIT 99999999999999999999", []mark{{"warning", "99999999999999999999"}}},
		{"SELECT * FROM o LIMIT", []mark{{"warning", "LIMIT"}}},
		{"SELECT * FROM o LIMIT 10;", nil},
		{"SELECT * FROM orders WHERE total = $1", []mark{{"error", "$1"}}},
		{"SELECT customer.offset, a.limit, b.lower FROM orders LIMIT 20", nil},
		{"SELECT * FROM union-items LIMIT 5", nil},
		{"SELECT * FROM distinct LIMIT 5", nil},
		{"SELECT offset FROM items ORDER BY offset LIMIT 20", nil},
		{"SELECT limit FROM items LIMIT 20", nil},
		{"SELECT distinct, as FROM items WHERE case = 1 AND not IS NULL", nil},
		{"SELECT * FROM items ORDER BY a OFFSET 5", []mark{{"warning", "OFFSET"}}},
		{"SELECT a AS b FROM items", []mark{{"warning", "AS"}}},
	} {
		var got []mark
		units := []rune(tc.sql)
		for _, d := range sqlDiagnostics(tc.sql) {
			// Offsets count UTF-16 units; the test strings keep astral runes
			// inside literals, so convert through UTF-16.
			text := string(utf16Slice(units, d.From, d.To))
			got = append(got, mark{d.Severity, text})
			if d.Message == "" {
				t.Errorf("%q: diagnostic without a message", tc.sql)
			}
		}
		if !reflect.DeepEqual(got, tc.want) {
			t.Errorf("%q: diagnostics %v, want %v", tc.sql, got, tc.want)
		}
	}
	if d := sqlDiagnostics("SELECT * FROM orders INNER JOIN users"); len(d) != 1 || !strings.Contains(d[0].Message, "Joins") {
		t.Errorf("JOIN diagnostics = %+v", d)
	}
}

// utf16Slice returns the runes between two UTF-16 offsets.
func utf16Slice(runes []rune, from, to int) []rune {
	var out []rune
	n := 0
	for _, r := range runes {
		if n >= from && n < to {
			out = append(out, r)
		}
		n++
		if r > 0xFFFF {
			n++
		}
	}
	return out
}

func TestRunHistoryIsBoundedNewestFirst(t *testing.T) {
	history := ranSQL(sqlHistory(`["SELECT 2","SELECT 1"]`), "SELECT 1")
	if want := []string{"SELECT 1", "SELECT 2"}; !reflect.DeepEqual(history, want) {
		t.Fatalf("history = %v, want %v", history, want)
	}
	for i := range 30 {
		history = ranSQL(history, strings.Repeat("x", 300)+strconv.Itoa(i))
	}
	if len(history) > maxSQLHistory || len(historyJSON(history)) > maxSQLHistoryBytes || history[0] != strings.Repeat("x", 300)+"29" {
		t.Fatalf("history keeps %d statements in %d bytes, newest %q", len(history), len(historyJSON(history)), history[0][300:])
	}
	for _, raw := range []string{"", "{}", `[1]`, strings.Repeat(" ", maxSQLHistoryBytes+1)} {
		if got := sqlHistory(raw); got != nil {
			t.Errorf("sqlHistory(%.20q) = %v, want none", raw, got)
		}
	}
	if got := ranSQL([]string{"SELECT 1"}, strings.Repeat("x", maxSQLHistoryBytes)); !reflect.DeepEqual(got, []string{"SELECT 1"}) {
		t.Errorf("a statement too large for the history replaced it: %d statements", len(got))
	}
	if got := ranSQL(nil, ""); got != nil {
		t.Errorf("an empty statement entered the history: %v", got)
	}
}

func TestRunHistoryPostsBackWithEveryConsoleForm(t *testing.T) {
	page := render(t, pageData{Section: "sql", CanWrite: true, SQL: "SELECT 1", SQLHistory: []string{"SELECT 1"}})
	dialog := page[strings.Index(page, `<dialog id="save-query"`):]
	for name, html := range map[string]string{"console": page[:strings.Index(page, `<dialog id="save-query"`)], "save dialog": dialog} {
		if !strings.Contains(html, `name="history" value="[&#34;SELECT 1&#34;]"`) {
			t.Errorf("the %s form does not post the run history back", name)
		}
	}
}
