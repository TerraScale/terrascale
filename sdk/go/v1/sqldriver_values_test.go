package v1_test

import (
	"net/http"
	"strings"
	"testing"
	"time"

	tb "terrascale.tech/sdk/v1"
)

func TestOpenDBDriverTimeValues(t *testing.T) {
	created := time.Date(2026, 3, 4, 5, 6, 7, 800000000, time.FixedZone("UTC+2", 2*3600))
	var wire string
	d, _ := sqlServerSeq(t, func(body string) { wire = body },
		sqlReply{http.StatusOK, `{"results":[{"columns":[],"rows":[],"rows_affected":1,"last_insert_rowid":null}]}`},
		sqlReply{http.StatusOK, `{"results":[{"columns":[{"name":"created_at","decltype":"DATETIME"},{"name":"n","decltype":"INTEGER"}],"rows":[["2026-03-04 03:06:07.8+00:00",42]],"rows_affected":0,"last_insert_rowid":null}]}`},
	)
	db := tb.OpenDB(d)
	defer db.Close()
	if _, err := db.Exec("INSERT INTO events (created_at) VALUES (?)", created); err != nil {
		t.Fatalf("insert: %v", err)
	}
	if !strings.Contains(wire, `"args":["2026-03-04 03:06:07.8+00:00"]`) {
		t.Fatalf("wire params: %s", wire)
	}
	rows, err := db.Query("SELECT created_at, n FROM events")
	if err != nil {
		t.Fatalf("query: %v", err)
	}
	defer rows.Close()
	var got time.Time
	var n int64
	if rows.Next() {
		if err := rows.Scan(&got, &n); err != nil {
			t.Fatalf("scan: %v", err)
		}
	}
	if err := rows.Err(); err != nil {
		t.Fatalf("rows: %v", err)
	}
	_, off := got.Zone()
	if !got.Equal(created) || off != 0 {
		t.Fatalf("created_at=%v n=%d", got, n)
	}
	if n != 42 {
		t.Fatalf("n=%d", n)
	}
	types, err := rows.ColumnTypes()
	if err != nil {
		t.Fatalf("column types: %v", err)
	}
	if types[0].DatabaseTypeName() != "DATETIME" || types[1].DatabaseTypeName() != "INTEGER" {
		t.Fatalf("decltypes: %q %q", types[0].DatabaseTypeName(), types[1].DatabaseTypeName())
	}
}
