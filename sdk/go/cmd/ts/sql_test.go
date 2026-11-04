package main

import (
	"bytes"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"

	v1 "terrascale.tech/sdk/v1"
)

func sqlTestDB(t *testing.T, handler http.HandlerFunc) *v1.Database {
	t.Helper()
	server := httptest.NewServer(handler)
	t.Cleanup(server.Close)
	c, err := v1.New(v1.Config{BaseURL: server.URL, Token: "fixture.jwt.signature"})
	if err != nil {
		t.Fatal(err)
	}
	return c.Database("01ARZ3NDEKTSV4RRFFQ69G5FA8", "main")
}

func TestSQLStdin(t *testing.T) {
	t.Parallel()
	const sql = "SELECT\n  9007199254740993 AS n"
	db := sqlTestDB(t, func(w http.ResponseWriter, r *http.Request) {
		var body struct {
			Statements []v1.Statement `json:"statements"`
		}
		if err := json.NewDecoder(r.Body).Decode(&body); err != nil {
			t.Error(err)
		}
		if !strings.HasSuffix(r.URL.Path, "/sql/execute") || len(body.Statements) != 1 || body.Statements[0].SQL != sql {
			t.Errorf("request=%s body=%+v", r.URL.Path, body)
		}
		fmt.Fprint(w, `{"results":[{"columns":[{"name":"n","decltype":null}],"rows":[[9007199254740993]],"rows_affected":0,"last_insert_rowid":null}]}`)
	})
	for _, structured := range []bool{false, true} {
		var buf bytes.Buffer
		var out io.Writer = &buf
		if structured {
			out = &jsonOutput{Writer: out}
		}
		if err := runSQL(t.Context(), db, "-", strings.NewReader(sql), out); err != nil {
			t.Fatal(err)
		}
		if !strings.Contains(buf.String(), "9007199254740993") || !json.Valid(buf.Bytes()) {
			t.Fatalf("output=%s", buf.String())
		}
	}
}
