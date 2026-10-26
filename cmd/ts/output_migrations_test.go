package main

import (
	"bytes"
	"encoding/json"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"testing"

	v1 "terrascale.tech/sdk/v1"
)

func TestJSONMigrationsAndCompletion(t *testing.T) {
	t.Parallel()
	dir := t.TempDir()
	var buf bytes.Buffer
	if err := newMigrationIn(dir, "create users", &jsonOutput{Writer: &buf}); err != nil {
		t.Fatal(err)
	}
	var created struct{ Path string }
	if err := json.Unmarshal(buf.Bytes(), &created); err != nil {
		t.Fatal(err)
	}
	if created.Path != filepath.Join(dir, "0001_create_users.sql") {
		t.Fatal("created path mismatch")
	}
	if _, err := os.Stat(created.Path); err != nil {
		t.Fatal(err)
	}
	s := jsonDatabaseSession(t, func(w http.ResponseWriter, r *http.Request) {
		var req struct{ Statements []v1.SQLStatement }
		if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
			t.Error(err)
		}
		results := make([]map[string]any, 0, len(req.Statements))
		for _, statement := range req.Statements {
			result := map[string]any{"columns": []any{}, "rows": []any{}, "rows_affected": 0, "last_insert_rowid": nil}
			if strings.HasPrefix(statement.SQL, "SELECT name, checksum") {
				result["columns"] = []map[string]string{{"name": "name", "decltype": "TEXT"}, {"name": "checksum", "decltype": "TEXT"}}
				result["rows"] = [][]string{{"0001_applied.sql", "sum"}}
			}
			results = append(results, result)
		}
		json.NewEncoder(w).Encode(map[string]any{"results": results})
	})
	db := s.tb.Database(s.project, "app")
	buf.Reset()
	if err := applyMigrations(t.Context(), db, nil, &jsonOutput{Writer: &buf}); err != nil || buf.String() != "{\"database\":\"app\",\"applied\":[]}\n" {
		t.Fatalf("migration summary: %v", err)
	}
	buf.Reset()
	files := []migration{{name: "0001_applied.sql", checksum: "changed"}, {name: "0002_pending.sql"}}
	if err := migrationStatus(t.Context(), db, files, &jsonOutput{Writer: &buf}); err != nil {
		t.Fatal(err)
	}
	if !json.Valid(buf.Bytes()) || !strings.Contains(buf.String(), `"state":"changed"`) || !strings.Contains(buf.String(), `"state":"pending"`) {
		t.Fatal("status metadata missing")
	}
	for _, shell := range []string{"bash", "zsh", "fish"} {
		buf.Reset()
		if err := run(t.Context(), []string{"completion", shell, "--json"}, &buf); err != nil {
			t.Fatal(err)
		}
		var script struct{ Shell, Script string }
		if err := json.Unmarshal(buf.Bytes(), &script); err != nil || script.Shell != shell || script.Script == "" {
			t.Fatal("completion asset missing")
		}
	}
	if err := run(t.Context(), []string{"completion", "unsupported", "--json"}, io.Discard); exitStatus(err) != 2 {
		t.Fatal("unsupported shell exit changed")
	}
}
