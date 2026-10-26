package v1_test

import (
	"encoding/json"
	"net/http"
	"strings"
	"testing"

	tb "terrascale.tech/sdk/v1"
)

func TestDocumentQueryExplain(t *testing.T) {
	for _, indexed := range []bool{false, true} {
		explain := `{"plan":["SCAN items"],"indexes":[],"full_scan":true,"rows_scanned":9007199254740993,"work_units":9223372036854775807,"elapsed_ms":0.125,"suggested_index":["status"],"future":true}`
		if indexed {
			explain = `{"plan":["SEARCH items USING INDEX tb_index_by_status"],"indexes":["by_status"],"full_scan":false,"rows_scanned":0,"work_units":0,"elapsed_ms":0}`
		}
		response := `{"items":[],"next_cursor":"continue","explain":` + explain + `}`
		col := parityClient(t, http.MethodPost, parityDatabase+"/collections/articles/query", "", `{"explain":true}`, response, http.StatusOK).Database(parityProject, "main").Collection("articles")
		page, err := col.Query(t.Context(), tb.Query{Explain: true})
		if err != nil {
			t.Fatal(err)
		}
		e := page.Explain
		if e == nil || page.NextCursor != "continue" || e.FullScan == indexed || len(e.Plan) != 1 {
			t.Fatalf("page=%+v", page)
		}
		if !indexed && (e.RowsScanned != 9007199254740993 || e.WorkUnits != 9223372036854775807 || e.ElapsedMs != 0.125 || len(e.SuggestedIndex) != 1) {
			t.Fatalf("explain=%+v", e)
		}
		if indexed && (len(e.Indexes) != 1 || e.SuggestedIndex != nil) {
			t.Fatalf("explain=%+v", e)
		}
	}
	plain, err := json.Marshal(tb.Query{})
	if err != nil || strings.Contains(string(plain), "explain") {
		t.Fatalf("ordinary query=%s error=%v", plain, err)
	}
	var page tb.QueryResult
	if err := json.Unmarshal([]byte(`{"items":[]}`), &page); err != nil || page.Explain != nil {
		t.Fatalf("ordinary response=%+v error=%v", page, err)
	}
}
