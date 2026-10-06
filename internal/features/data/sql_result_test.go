package data

import (
	"encoding/json/jsontext"
	"errors"
	"fmt"
	"slices"
	"strings"
	"testing"

	"terrascale.tech/dashboard/internal/core"
)

func TestSQLResultColumnsFollowFirstUse(t *testing.T) {
	result := newSQLResult([]documentView{
		{ID: "ord_1", Value: jsontext.Value(`{"total":725.5,"status":"paid","tags":["a"]}`)},
		{ID: "ord_2", Value: jsontext.Value(`{"status":"open","note":null,"customer":{"id":"cus_2"}}`)},
		{ID: "ord_3", Value: jsontext.Value(`"legacy"`)},
	}, 3)

	want := []sqlColumn{{"total", true, false}, {"status", false, false}, {"tags", false, false}, {"note", false, false}, {"customer", false, false}, {"value", false, true}}
	if !slices.Equal(result.Columns, want) {
		t.Fatalf("columns = %v, want %v", result.Columns, want)
	}
	cells := func(row int) []string {
		var out []string
		for _, cell := range result.Rows[row].Cells {
			out = append(out, cell.Kind+":"+cell.Text)
		}
		return out
	}
	cellsWant := [][]string{
		{"number:725.5", `string:paid`, `array:["a"]`, "missing:", "missing:", "missing:"},
		{"missing:", "string:open", "missing:", "null:null", `object:{"id":"cus_2"}`, "missing:"},
		{"missing:", "missing:", "missing:", "missing:", "missing:", "string:legacy"},
	}
	for row := range cellsWant {
		if got := cells(row); !slices.Equal(got, cellsWant[row]) {
			t.Errorf("row %d cells = %q, want %q", row, got, cellsWant[row])
		}
	}
	if result.Truncated() || result.HiddenColumns != 0 {
		t.Fatalf("a small result is shown whole: %+v", result)
	}
	if !strings.Contains(result.JSON, `"id": "ord_2"`) || !strings.Contains(result.JSON, "\n  ") {
		t.Fatalf("raw JSON must hold the rows indented, got %s", result.JSON)
	}
}

func TestSQLResultBoundsLargeResults(t *testing.T) {
	var fields []string
	for i := range maxResultColumns + 3 {
		fields = append(fields, fmt.Sprintf(`"f%02d":%d`, i, i))
	}
	long := strings.Repeat("é", maxCellRunes+40)
	rows := make([]documentView, maxResultRows+50)
	for i := range rows {
		rows[i] = documentView{ID: fmt.Sprintf("doc_%04d", i), Value: jsontext.Value(`{` + strings.Join(fields, ",") + `,"long":"` + long + `"}`)}
	}
	result := newSQLResult(rows, 10_000)

	if len(result.Rows) != maxResultRows || result.Matched != 10_000 || !result.Truncated() {
		t.Fatalf("rows = %d, matched = %d; want %d of 10000", len(result.Rows), result.Matched, maxResultRows)
	}
	if len(result.Columns) != maxResultColumns || result.HiddenColumns != 4 {
		t.Fatalf("columns = %d, hidden = %d; want %d and 4", len(result.Columns), result.HiddenColumns, maxResultColumns)
	}
	if strings.Contains(result.JSON, "doc_0200") {
		t.Fatal("the raw view holds only the rows the table shows")
	}

	cell := formatCell(jsontext.Value(`"` + long + `"`))
	if n := len([]rune(cell.Text)); n != maxCellRunes || !strings.HasSuffix(cell.Text, "…") || cell.Title == "" {
		t.Fatalf("a long value is cut to %d runes with the longer text as its title; got %d runes, title %q", maxCellRunes, n, cell.Title)
	}
	digits := strings.Repeat("9", maxCellRunes+10)
	if number := formatCell(jsontext.Value(digits)); number.Text != digits {
		t.Fatalf("numbers are never cut, got %q", number.Text)
	}
}

func TestSQLResultReportsMatchedRowsBeyondTheRowsReturned(t *testing.T) {
	result := newSQLResult([]documentView{{ID: "a", Value: jsontext.Value(`{}`)}}, 0)
	if result.Matched != 1 || result.Truncated() {
		t.Fatalf("matched is at least the rows returned, got %+v", result)
	}
	if empty := newSQLResult(nil, 0); len(empty.Rows) != 0 || len(empty.Columns) != 0 || empty.JSON != "[]" {
		t.Fatalf("an empty result has no rows or columns, got %+v", empty)
	}
}

func TestDemoSelectReportsTheRowsItsDefaultCapLeavesOut(t *testing.T) {
	documents := make(map[string][]byte, maxDemoRows+20)
	for i := range maxDemoRows + 20 {
		documents[fmt.Sprintf("doc_%03d", i)] = []byte(`{}`)
	}
	db := core.Database{Documents: map[string]map[string][]byte{"orders": documents}}

	rows, matched, err := demoSelect(db, "SELECT * FROM orders")
	if err != nil || len(rows) != maxDemoRows || matched != maxDemoRows+20 || rows[0].ID != "doc_000" {
		t.Fatalf("without a LIMIT: %d rows, matched %d, %v; want the first %d of %d", len(rows), matched, err, maxDemoRows, maxDemoRows+20)
	}
	if !newSQLResult(rows, matched).Truncated() {
		t.Fatal("a query cut by the demo cap says so")
	}
	if rows, matched, err = demoSelect(db, "SELECT * FROM orders LIMIT 5"); err != nil || len(rows) != 5 || matched != 5 {
		t.Fatalf("LIMIT 5: %d rows, matched %d, %v; the LIMIT is part of the query", len(rows), matched, err)
	}
	for _, limit := range []string{"0", "101"} {
		if _, _, err := demoSelect(db, "SELECT * FROM orders LIMIT "+limit); !errors.Is(err, core.ErrInvalid) {
			t.Errorf("LIMIT %s is outside 1 to %d and is refused, got %v", limit, maxDemoRows, err)
		}
	}
	if _, _, err := demoSelect(db, "SELECT id FROM orders"); !errors.Is(err, core.ErrInvalid) {
		t.Errorf("a statement the demo cannot run is refused, got %v", err)
	}
}

func TestParseSchemaExplainsWhatToFix(t *testing.T) {
	cases := []struct {
		name, body, want string
	}{
		{"valid", `{"name":"orders","fields":[{"name":"total","type":"float","required":true,"indexed":false}],"historyRetentionDays":7,"mergePolicy":"last-write-wins"}`, ""},
		{"starter", starterSchema("orders"), ""},
		{"not JSON", `{"name":`, "not valid JSON"},
		{"JSON Schema", `{"type":"object","properties":{}}`, `"name" must be "orders"`},
		{"other collection", `{"name":"users","fields":[],"historyRetentionDays":7,"mergePolicy":"remove-wins"}`, `"name" must be "orders"`},
		{"retention", `{"name":"orders","fields":[],"historyRetentionDays":90,"mergePolicy":"remove-wins"}`, "history retention must be between 1 and 30 days"},
		{"field type", `{"name":"orders","fields":[{"name":"total","type":"money"}],"historyRetentionDays":7,"mergePolicy":"remove-wins"}`, `unknown field type "money"`},
		{"empty", ``, "at most 128 KiB"},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			_, problem := parseSchema("orders", []byte(tc.body))
			if tc.want == "" && problem != "" || tc.want != "" && !strings.Contains(problem, tc.want) {
				t.Fatalf("problem = %q, want %q", problem, tc.want)
			}
		})
	}
}
