package v1_test

import (
	"net/http"
	"testing"

	tb "terrascale.tech/sdk/v1"
)

func TestOpenDBDriver(t *testing.T) {
	d, _ := sqlServerSeq(t, nil,
		sqlReply{http.StatusOK, `{"results":[{"columns":[{"name":"1","decltype":null}],"rows":[[1]],"rows_affected":0,"last_insert_rowid":null}]}`},
		sqlReply{http.StatusOK, `{"results":[{"columns":[],"rows":[],"rows_affected":1,"last_insert_rowid":7}]}`},
		sqlReply{http.StatusOK, `{"results":[{"columns":[{"name":"id","decltype":"TEXT"},{"name":"name","decltype":"TEXT"}],"rows":[["01JA0000000000000000000001","Ada"]],"rows_affected":0,"last_insert_rowid":null}]}`},
	)
	db := tb.OpenDB(d)
	defer db.Close()
	var one int64
	if err := db.QueryRow("SELECT 1").Scan(&one); err != nil || one != 1 {
		t.Fatalf("select 1: one=%d err=%v", one, err)
	}
	res, err := db.Exec("INSERT INTO people (id, name) VALUES (?, ?)", "01JA0000000000000000000001", "Ada")
	if err != nil {
		t.Fatalf("insert: %v", err)
	}
	if id, _ := res.LastInsertId(); id != 7 {
		t.Fatalf("last insert id=%d", id)
	}
	if n, _ := res.RowsAffected(); n != 1 {
		t.Fatalf("rows affected=%d", n)
	}
	rows, err := db.Query("SELECT id, name FROM people WHERE id = ?", "01JA0000000000000000000001")
	if err != nil {
		t.Fatalf("query: %v", err)
	}
	defer rows.Close()
	var id, name string
	for rows.Next() {
		if err := rows.Scan(&id, &name); err != nil {
			t.Fatalf("scan: %v", err)
		}
	}
	if err := rows.Err(); err != nil {
		t.Fatalf("rows: %v", err)
	}
	if id != "01JA0000000000000000000001" || name != "Ada" {
		t.Fatalf("id=%s name=%s", id, name)
	}
}
