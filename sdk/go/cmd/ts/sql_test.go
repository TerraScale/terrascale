package main

import (
	"bytes"

	"encoding/json"

	"fmt"
	"io"
	"net/http"
	"strings"
	"testing"

	v1 "terrascale.tech/sdk/v1"
)

func TestSQLStdinAndModes(t *testing.T) {
	t.Parallel()
	for _, mode := range []string{"relational", "document"} {
		t.Run(mode, func(t *testing.T) {
			t.Parallel()
			const sql = "SELECT\n  9007199254740993 AS n"
			db := exportTestDB(t, func(w http.ResponseWriter, r *http.Request) {
				var body struct {
					SQL        string            `json:"sql"`
					Statements []v1.SQLStatement `json:"statements"`
				}
				if err := json.NewDecoder(r.Body).Decode(&body); err != nil {
					t.Error(err)
				}
				if mode == "relational" {
					if !strings.HasSuffix(r.URL.Path, "/sql/execute") || len(body.Statements) != 1 || body.Statements[0].SQL != sql {
						t.Errorf("request=%s body=%+v", r.URL.Path, body)
					}
					fmt.Fprint(w, `{"results":[{"columns":[{"name":"n","decltype":null}],"rows":[[9007199254740993]],"rows_affected":0,"last_insert_rowid":null}]}`)
				} else {
					if !strings.HasSuffix(r.URL.Path, "/sql") || body.SQL != sql {
						t.Errorf("request=%s body=%+v", r.URL.Path, body)
					}
					fmt.Fprint(w, `{"matched":1,"truncated":false,"rows":[{"id":"01ARZ3NDEKTSV4RRFFQ69G5FD0","value":{"n":9007199254740993}}]}`)
				}
			})
			for _, structured := range []bool{false, true} {
				var buf bytes.Buffer
				var out io.Writer = &buf
				if structured {
					out = &jsonOutput{Writer: out}
				}
				if err := runSQL(t.Context(), db, mode, "-", strings.NewReader(sql), out); err != nil {
					t.Fatal(err)
				}
				if !strings.Contains(buf.String(), "9007199254740993") || !json.Valid(buf.Bytes()) {
					t.Fatalf("output=%s", buf.String())
				}
			}
		})
	}
}
