package v1_test

import (
	"encoding/json"
	"net/http"
	"os"
	"testing"
)

func TestPinnedSQLContractFixture(t *testing.T) {
	data, err := os.ReadFile("testdata/p10-contract-provenance.json")
	if err != nil {
		t.Fatal(err)
	}
	var fixture struct {
		Revision string
		SQL      struct{ Success json.RawMessage }
	}
	if err := json.Unmarshal(data, &fixture); err != nil {
		t.Fatal(err)
	}
	if fixture.Revision != "1d823d8534fc5035859a47e263691d0c7cb7b744" {
		t.Fatal("producer revision changed without contract review")
	}
	db, _ := sqlServer(t, http.StatusOK, string(fixture.SQL.Success), nil)
	rows, err := db.Query(t.Context(), "SELECT id FROM t")
	if err != nil || len(rows) != 1 || rows[0].Map()["id"] != "customer/chosen" {
		t.Fatalf("rows=%v error=%v", rows, err)
	}
}
