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
	s := jsonDatabaseSession(t, func(w http.ResponseWriter, r *http.Request) {
		body := new(bytes.Buffer)
		body.ReadFrom(r.Body)
		if !strings.Contains(body.String(), "SELECT 42") {
			t.Error("supplied stdin did not reach the SDK")
		}
		fmt.Fprint(w, `{"results":[{"columns":[],"rows":[],"rows_affected":0,"last_insert_rowid":null}]}`)
	})
	var out bytes.Buffer
	input := commandInput{in: strings.NewReader("SELECT 42")}
	if err := s.dispatch(t.Context(), "sql", functionFlags{}, []string{"app", "-"}, input, &jsonOutput{Writer: &out}); err != nil || !json.Valid(out.Bytes()) {
		t.Fatalf("command output=%s error=%v", out.String(), err)
	}
}
