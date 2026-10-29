package main

import (
	"bytes"
	"encoding/json"
	"fmt"
	"net/http"
	"strings"
	"testing"
)

func TestSQLCommandUsesSuppliedStdin(t *testing.T) {
	t.Parallel()
	for _, name := range []string{"sql", "exec"} {
		t.Run(name, func(t *testing.T) {
			t.Parallel()
			s := jsonDatabaseSession(t, func(w http.ResponseWriter, r *http.Request) {
				body := new(bytes.Buffer)
				body.ReadFrom(r.Body)
				if !strings.Contains(body.String(), "SELECT 42") {
					t.Error("supplied stdin did not reach the SDK")
				}
				if name == "sql" {
					fmt.Fprint(w, `{"matched":0,"truncated":false,"rows":[]}`)
				} else {
					fmt.Fprint(w, `{"results":[{"columns":[],"rows":[],"rows_affected":0,"last_insert_rowid":null}]}`)
				}
			})
			var out bytes.Buffer
			input := commandInput{in: strings.NewReader("SELECT 42")}
			if err := s.dispatch(t.Context(), name, functionFlags{}, "", []string{"app", "-"}, input, &jsonOutput{Writer: &out}); err != nil || !json.Valid(out.Bytes()) {
				t.Fatalf("command output=%s error=%v", out.String(), err)
			}
		})
	}
}
