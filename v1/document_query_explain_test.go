package v1_test

import (
	"encoding/json"
	"fmt"
	"net/http"
	"reflect"
	"strings"
	"testing"

	tb "terrascale.tech/sdk/v1"
)

// Contract authority: a3cbf8b1c73d304189a489fa8033d41f3b508de3,
// DocumentModels.cs ExplainBody and QueryDocumentsEndpoint.cs (see contracts/document-query-explain.md).
func TestDocumentQueryExplainRequest(t *testing.T) {
	for _, enabled := range []bool{false, true} {
		t.Run(fmt.Sprint(enabled), func(t *testing.T) {
			body := `{}`
			reply := `{"items":[],"next_cursor":null}`
			if enabled {
				body = `{"explain":true}`
				reply = `{"items":[],"next_cursor":null,"explain":null}`
			}
			col := parityClient(t, http.MethodPost, parityDatabase+"/collections/users/query", "", body, reply, http.StatusOK).Database(parityProject, "main").Collection("users")
			out, err := col.Query(t.Context(), tb.Query{Explain: enabled})
			if err != nil || out.Explain != nil || out.NextCursor != "" {
				t.Fatalf("page=%+v err=%v", out, err)
			}
			raw, err := json.Marshal(out)
			var fields map[string]json.RawMessage
			if err != nil || json.Unmarshal(raw, &fields) != nil || fields["explain"] != nil {
				t.Fatalf("absent diagnostics must be omitted: %s, %v", raw, err)
			}
		})
	}
}

func TestDocumentQueryExplainResponse(t *testing.T) {
	for _, suggestion := range []string{"", `,"suggested_index":null`, `,"suggested_index":["status"]`} {
		t.Run(suggestion, func(t *testing.T) {
			// Boundary integers prove exact int64 handling, not a reachable work budget.
			diagnostics := `{"plan":["SCAN items"],"indexes":[],"full_scan":true,"rows_scanned":9007199254740993,"work_units":9223372036854775807,"elapsed_ms":0.125` + suggestion + `}`
			reply := `{"items":[],"next_cursor":"continuation","explain":` + diagnostics + `}`
			col := parityClient(t, http.MethodPost, parityDatabase+"/collections/users/query", "", `{"explain":true}`, reply, http.StatusOK).Database(parityProject, "main").Collection("users")
			out, err := col.Query(t.Context(), tb.Query{Explain: true})
			if err != nil {
				t.Fatal(err)
			}
			x := out.Explain
			if x == nil || !reflect.DeepEqual(x.Plan, []string{"SCAN items"}) || x.Indexes == nil || len(x.Indexes) != 0 || !x.FullScan || x.RowsScanned != 9007199254740993 || x.WorkUnits != 9223372036854775807 || x.ElapsedMs != 0.125 || out.NextCursor != "continuation" {
				t.Fatalf("explain=%+v cursor=%q", x, out.NextCursor)
			}
			var want []string
			if suggestion == `,"suggested_index":["status"]` {
				want = []string{"status"}
			}
			if !reflect.DeepEqual(x.SuggestedIndex, want) {
				t.Fatalf("suggested_index=%v; want %v", x.SuggestedIndex, want)
			}
			raw, err := json.Marshal(x)
			if err != nil {
				t.Fatal(err)
			}
			var fields map[string]json.RawMessage
			if err := json.Unmarshal(raw, &fields); err != nil {
				t.Fatal(err)
			}
			if string(fields["rows_scanned"]) != "9007199254740993" || string(fields["work_units"]) != "9223372036854775807" || (fields["suggested_index"] != nil) != (want != nil) {
				t.Fatalf("roundtrip=%s", raw)
			}
		})
	}
}

func TestDocumentQueryExplainPages(t *testing.T) {
	calls := 0
	db := changesDB(t, func(w http.ResponseWriter, r *http.Request) {
		calls++
		var body map[string]json.RawMessage
		if err := json.NewDecoder(r.Body).Decode(&body); err != nil {
			t.Error(err)
		}
		wantCursor, next := "", `"next"`
		if calls > 1 {
			wantCursor, next = `"next"`, "null"
		}
		if string(body["explain"]) != "true" || string(body["cursor"]) != wantCursor || (body["count"] != nil) != (calls == 1) {
			t.Errorf("page %d request=%v", calls, body)
		}
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprintf(w, `{"items":[],"next_cursor":%s,"explain":{"plan":["SEARCH items USING INDEX tb_index_by_status"],"indexes":["tb_index_by_status"],"full_scan":false,"rows_scanned":0,"work_units":0,"elapsed_ms":0}}`, next)
	})
	pages := 0
	for page, err := range db.Collection("users").Pages(t.Context(), tb.Query{Explain: true, Count: true}) {
		if err != nil {
			t.Fatal(err)
		}
		if page.Explain == nil || page.Explain.FullScan || len(page.Explain.Indexes) != 1 || page.Explain.Indexes[0] != "tb_index_by_status" {
			t.Fatalf("page=%+v", page)
		}
		pages++
	}
	if pages != 2 || calls != 2 {
		t.Fatalf("pages=%d calls=%d", pages, calls)
	}
}

func TestDocumentQueryExplainRejectsMalformedMembers(t *testing.T) {
	const valid = `{"plan":[],"indexes":[],"full_scan":false,"rows_scanned":0,"work_units":0,"elapsed_ms":0}`
	assertInvalid := func(t *testing.T, diagnostics string) {
		t.Helper()
		reply := `{"items":[],"next_cursor":null,"explain":` + diagnostics + `}`
		col := parityClient(t, http.MethodPost, parityDatabase+"/collections/users/query", "", `{"explain":true}`, reply, http.StatusOK).Database(parityProject, "main").Collection("users")
		if out, err := col.Query(t.Context(), tb.Query{Explain: true}); err == nil || out != nil || !strings.Contains(err.Error(), "invalid query explain response") || strings.Contains(err.Error(), "private-fixture") {
			t.Fatalf("malformed diagnostics result=%+v err=%v", out, err)
		}
	}
	for _, name := range []string{"plan", "indexes", "full_scan", "rows_scanned", "work_units", "elapsed_ms"} {
		for _, mode := range []string{"missing", "null", "wrong-type"} {
			t.Run(name+"/"+mode, func(t *testing.T) {
				var fields map[string]json.RawMessage
				if err := json.Unmarshal([]byte(valid), &fields); err != nil {
					t.Fatal(err)
				}
				switch mode {
				case "missing":
					delete(fields, name)
				case "null":
					fields[name] = json.RawMessage(`null`)
				default:
					fields[name] = json.RawMessage(`"private-fixture"`)
				}
				body, err := json.Marshal(fields)
				if err != nil {
					t.Fatal(err)
				}
				assertInvalid(t, string(body))
			})
		}
	}
	for _, diagnostics := range []string{`{}`, `[]`,
		strings.Replace(valid, `"plan":[]`, `"plan":[null]`, 1),
		strings.Replace(valid, `"indexes":[]`, `"indexes":[null]`, 1),
		strings.TrimSuffix(valid, "}") + `,"suggested_index":[null]}`,
		strings.TrimSuffix(valid, "}") + `,"suggested_index":[1]}`,
		strings.Replace(valid, `"rows_scanned":0`, `"rows_scanned":9223372036854775808`, 1),
		strings.Replace(valid, `"work_units":0`, `"work_units":0.5`, 1),
	} {
		t.Run(diagnostics, func(t *testing.T) { assertInvalid(t, diagnostics) })
	}
}

func TestDocumentQueryExplainAllowsZeroAndEmptyMembers(t *testing.T) {
	reply := `{"items":[],"next_cursor":null,"explain":{"plan":[],"indexes":[],"full_scan":false,"rows_scanned":0,"work_units":0,"elapsed_ms":0,"suggested_index":[],"future_member":{"value":null},"Plan":[null],"Suggested_Index":[null]}}`
	col := parityClient(t, http.MethodPost, parityDatabase+"/collections/users/query", "", `{"explain":true}`, reply, http.StatusOK).Database(parityProject, "main").Collection("users")
	out, err := col.Query(t.Context(), tb.Query{Explain: true})
	if err != nil {
		t.Fatal(err)
	}
	x := out.Explain
	if x == nil || x.Plan == nil || len(x.Plan) != 0 || x.Indexes == nil || len(x.Indexes) != 0 || x.SuggestedIndex == nil || len(x.SuggestedIndex) != 0 || x.FullScan || x.RowsScanned != 0 || x.WorkUnits != 0 || x.ElapsedMs != 0 {
		t.Fatalf("zero diagnostics: %+v", x)
	}
}
