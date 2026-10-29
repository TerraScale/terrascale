package main

import (
	"bytes"
	"encoding/json"
	"io"
	"maps"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"slices"
	"strings"
	"sync"
	"testing"

	v1 "terrascale.tech/sdk/v1"
)

func TestSplitSQL(t *testing.T) {
	script := `-- users
CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT DEFAULT 'a;b'); /* c; */
UPDATE users SET name = "x;" WHERE name = '';
;
INSERT INTO users (name) VALUES ('it''s')
-- trailing comment;`
	want := []string{
		"-- users\nCREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT DEFAULT 'a;b')",
		"/* c; */\nUPDATE users SET name = \"x;\" WHERE name = ''",
		"INSERT INTO users (name) VALUES ('it''s')\n-- trailing comment;",
	}
	if got := splitSQL(script); !slices.Equal(got, want) {
		t.Fatalf("splitSQL = %q", got)
	}
}

func TestSchemaDiff(t *testing.T) {
	from := map[string]string{"table a": "table a (x TEXT)", "collection b": "collection b"}
	to := map[string]string{"table a": "table a (x TEXT, y INTEGER)", "index c": "index c ON a (y)"}
	want := "--- main\n+++ pr-1\n- collection b\n+ index c ON a (y)\n- table a (x TEXT)\n+ table a (x TEXT, y INTEGER)\n"
	if got := schemaDiff("main", "pr-1", from, to); got != want {
		t.Fatalf("diff = %q", got)
	}
	if got := schemaDiff("main", "pr-1", from, from); got != "pr-1 has the schema of main\n" {
		t.Fatalf("same = %q", got)
	}
}

// TestMigrateUpAppliesPendingOnce runs new, status, up and up again against
// a server that keeps the ts_migrations rows.
func TestMigrateUpAppliesPendingOnce(t *testing.T) {
	t.Chdir(t.TempDir())
	var mu sync.Mutex
	history := map[string]string{} // nil until CREATE TABLE
	created := false
	var batches [][]string
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		var req struct{ Statements []v1.SQLStatement }
		json.NewDecoder(r.Body).Decode(&req)
		mu.Lock()
		defer mu.Unlock()
		w.Header().Set("Content-Type", "application/json")
		var sqls []string
		results := make([]map[string]any, len(req.Statements))
		for i, st := range req.Statements {
			sqls = append(sqls, st.SQL)
			results[i] = map[string]any{"columns": []any{}, "rows": []any{}, "rows_affected": 0, "last_insert_rowid": nil}
			switch {
			case strings.HasPrefix(st.SQL, "CREATE TABLE IF NOT EXISTS ts_migrations"):
				created = true
			case strings.HasPrefix(st.SQL, "SELECT name, checksum"):
				if !created {
					w.WriteHeader(http.StatusBadRequest)
					io.WriteString(w, `{"error":{"code":"sql_error","message":"no such table: ts_migrations"}}`)
					return
				}
				results[i]["columns"] = []map[string]any{{"name": "name", "decltype": "TEXT"}, {"name": "checksum", "decltype": "TEXT"}}
				rows := make([][]any, 0)
				for _, name := range slices.Sorted(maps.Keys(history)) {
					rows = append(rows, []any{name, history[name]})
				}
				results[i]["rows"] = rows
			case strings.HasPrefix(st.SQL, "INSERT INTO ts_migrations"):
				history[st.Args[0].(string)] = st.Args[1].(string)
			}
		}
		batches = append(batches, sqls)
		json.NewEncoder(w).Encode(map[string]any{"results": results})
	}))
	defer srv.Close()
	c, err := v1.New(v1.Config{BaseURL: srv.URL, Token: "tb_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"})
	if err != nil {
		t.Fatal(err)
	}
	s := &session{tb: c, project: "01M5N8CG630T6P449ES15GNZ8N"}
	for _, name := range []string{"Create users", "add orders"} {
		if err := run(t.Context(), []string{"migrate", "new", name}, io.Discard); err != nil {
			t.Fatal(err)
		}
	}
	os.WriteFile(filepath.Join("migrations", "0001_create_users.sql"), []byte("CREATE TABLE users (id INTEGER PRIMARY KEY);\n"), 0o644)
	os.WriteFile(filepath.Join("migrations", "0002_add_orders.sql"), []byte("CREATE TABLE orders (id INTEGER);\nCREATE INDEX o ON orders (id);\n"), 0o644)

	var out bytes.Buffer
	if err := s.run(t.Context(), "migrate status", []string{"main"}, &out); err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(out.String(), "0001_create_users.sql  pending") || created {
		t.Fatalf("status before up = %q, created = %v", out.String(), created)
	}
	out.Reset()
	if err := s.run(t.Context(), "migrate up", []string{"main"}, &out); err != nil {
		t.Fatal(err)
	}
	if out.String() != "Applied 0001_create_users.sql\nApplied 0002_add_orders.sql\n" || len(history) != 2 {
		t.Fatalf("up = %q, history = %v", out.String(), history)
	}
	if last := batches[len(batches)-1]; len(last) != 3 || last[1] != "CREATE INDEX o ON orders (id)" {
		t.Fatalf("last batch = %q", last)
	}
	out.Reset()
	if err := s.run(t.Context(), "migrate up", []string{"main"}, &out); err != nil || out.String() != "main is up to date\n" {
		t.Fatalf("second up = %q, %v", out.String(), err)
	}
	os.WriteFile(filepath.Join("migrations", "0001_create_users.sql"), []byte("CREATE TABLE users (id TEXT);\n"), 0o644)
	if err := s.run(t.Context(), "migrate up", []string{"main"}, io.Discard); err == nil || !strings.Contains(err.Error(), "changed after it was applied") {
		t.Fatalf("edited migration = %v", err)
	}
	out.Reset()
	if err := s.run(t.Context(), "migrate status", []string{"main"}, &out); err != nil ||
		!strings.Contains(out.String(), "0001_create_users.sql  changed") || !strings.Contains(out.String(), "0002_add_orders.sql    applied") {
		t.Fatalf("status = %q, %v", out.String(), err)
	}
}
