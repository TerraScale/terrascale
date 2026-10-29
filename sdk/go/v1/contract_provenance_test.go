package v1_test

import (
	"encoding/json"
	"net/http"
	"os"
	"testing"

	tb "terrascale.tech/sdk/v1"
)

func TestPinnedSQLAndExplainContractFixtures(t *testing.T) {
	data, err := os.ReadFile("testdata/p10-contract-provenance.json")
	if err != nil {
		t.Fatal(err)
	}
	var fixture struct {
		Revision string
		SQL      struct{ Success json.RawMessage }
		Query    struct{ Request, Success json.RawMessage }
	}
	if err := json.Unmarshal(data, &fixture); err != nil {
		t.Fatal(err)
	}
	if fixture.Revision != "1d823d8534fc5035859a47e263691d0c7cb7b744" {
		t.Fatal("producer revision changed without contract review")
	}
	db, _ := sqlServer(t, http.StatusOK, string(fixture.SQL.Success), nil)
	rows, err := db.QuerySQL(t.Context(), "SELECT id FROM t")
	if err != nil || len(rows) != 1 || rows[0]["id"] != "customer/chosen" {
		t.Fatalf("rows=%v error=%v", rows, err)
	}
	col := parityClient(t, http.MethodPost, parityDatabase+"/collections/articles/query", "", string(fixture.Query.Request), string(fixture.Query.Success), http.StatusOK).Database(parityProject, "main").Collection("articles")
	page, err := col.Query(t.Context(), tb.Query{Explain: true})
	if err != nil || page.Explain == nil || page.Explain.RowsScanned != 9007199254740993 {
		t.Fatalf("page=%v error=%v", page, err)
	}
}
