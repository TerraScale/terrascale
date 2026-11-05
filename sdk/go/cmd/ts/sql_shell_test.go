package main

import (
	"bytes"
	"encoding/json"
	"fmt"
	"net/http"
	"strings"
	"sync"
	"testing"
)

func TestSQLShellRunsEachRunBoundary(t *testing.T) {
	t.Parallel()
	const (
		createSQL = "CREATE TABLE people (id TEXT PRIMARY KEY,\n  note TEXT)\n"
		insertSQL = "INSERT INTO people (id, note)\nVALUES ('a1', 'semi;colon')\n"
		selectSQL = "SELECT id, note FROM people ORDER BY id;\n"
	)
	var mu sync.Mutex
	var requests []string
	s := jsonDatabaseSession(t, func(w http.ResponseWriter, r *http.Request) {
		var body struct {
			Statements []struct {
				SQL string `json:"sql"`
			} `json:"statements"`
		}
		if err := json.NewDecoder(r.Body).Decode(&body); err != nil {
			t.Error(err)
		}
		if !strings.HasSuffix(r.URL.Path, "/sql/execute") || len(body.Statements) != 1 {
			t.Errorf("request=%s statements=%+v", r.URL.Path, body.Statements)
		}
		mu.Lock()
		requests = append(requests, body.Statements[0].SQL)
		mu.Unlock()
		switch body.Statements[0].SQL {
		case createSQL:
			fmt.Fprint(w, `{"results":[{"columns":[],"rows":[],"rows_affected":0,"last_insert_rowid":null}]}`)
		case insertSQL:
			fmt.Fprint(w, `{"results":[{"columns":[],"rows":[],"rows_affected":1,"last_insert_rowid":1}]}`)
		case selectSQL:
			fmt.Fprint(w, `{"results":[{"columns":[{"name":"id","decltype":null},{"name":"note","decltype":null}],"rows":[["a1","semi;colon"]],"rows_affected":0,"last_insert_rowid":null}]}`)
		default:
			t.Errorf("unexpected statement %q", body.Statements[0].SQL)
		}
	})
	var out, prompt bytes.Buffer
	input := commandInput{in: strings.NewReader("CREATE TABLE people (id TEXT PRIMARY KEY,\n  note TEXT)\n.run\nINSERT INTO people (id, note)\nVALUES ('a1', 'semi;colon')\n.run\nSELECT id, note FROM people ORDER BY id;\n.run\n"), prompt: &prompt}
	if err := s.dispatch(t.Context(), "shell", functionFlags{}, []string{"app"}, input, &jsonOutput{Writer: &out}); err != nil {
		t.Fatal(err)
	}
	got := strings.Split(strings.TrimSpace(out.String()), "\n")
	if len(got) != 4 {
		t.Fatalf("stream records=%d output=%s", len(got), out.String())
	}
	for _, line := range got {
		if !json.Valid([]byte(line)) {
			t.Fatalf("invalid JSON stream line %q", line)
		}
	}
	if !strings.Contains(got[2], "semi;colon") || got[3] != `{"type":"complete"}` {
		t.Fatalf("select record=%s complete=%s", got[2], got[3])
	}
	mu.Lock()
	defer mu.Unlock()
	if len(requests) != 3 || requests[0] != createSQL || requests[1] != insertSQL || requests[2] != selectSQL {
		t.Fatalf("submitted statements=%q", requests)
	}
	if !strings.Contains(prompt.String(), "one SQL statement in its own transaction") {
		t.Fatalf("banner=%s", prompt.String())
	}
}
