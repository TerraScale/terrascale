package main

import (
	"bytes"
	"database/sql"
	"encoding/json"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"sync/atomic"
	"testing"

	v1 "terrascale.tech/sdk/v1"
)

// The REST fixture runs real SQLite transactions; it never runs TerraBase.
func schemaRESTFixture(t *testing.T, initial string) (*v1.Database, *sql.Conn, *atomic.Int32) {
	t.Helper()
	conn, closeModel, err := openSchemaModel(t.Context())
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(closeModel)
	if _, err := conn.ExecContext(t.Context(), initial); err != nil {
		t.Fatal(err)
	}
	writes := new(atomic.Int32)
	db := sqlTestDB(t, func(w http.ResponseWriter, r *http.Request) {
		var request struct {
			Statements []v1.Statement `json:"statements"`
		}
		if err := json.NewDecoder(r.Body).Decode(&request); err != nil {
			t.Error(err)
			http.Error(w, "invalid request", 400)
			return
		}
		tx, err := conn.BeginTx(r.Context(), nil)
		if err != nil {
			t.Error(err)
			return
		}
		defer tx.Rollback()
		results := []map[string]any{}
		for _, statement := range request.Statements {
			if !strings.HasPrefix(statement.SQL, "SELECT") {
				writes.Add(1)
			}
			rows, err := tx.QueryContext(r.Context(), statement.SQL, statement.Args...)
			if err != nil {
				t.Error(err)
				http.Error(w, "SQL refused", 400)
				return
			}
			names, err := rows.Columns()
			if err != nil {
				t.Error(err)
				return
			}
			columns := []map[string]any{}
			for _, name := range names {
				columns = append(columns, map[string]any{"name": name})
			}
			values := [][]any{}
			for rows.Next() {
				row := make([]any, len(names))
				ptrs := make([]any, len(names))
				for i := range row {
					ptrs[i] = &row[i]
				}
				if err := rows.Scan(ptrs...); err != nil {
					t.Error(err)
				}
				values = append(values, row)
			}
			if err := rows.Err(); err != nil {
				t.Error(err)
			}
			rows.Close()
			results = append(results, map[string]any{"columns": columns, "rows": values, "rows_affected": 0, "last_insert_rowid": nil})
		}
		if err := tx.Commit(); err != nil {
			t.Error(err)
			return
		}
		json.NewEncoder(w).Encode(map[string]any{"results": results})
	})
	return db, conn, writes
}

func TestSchemaPullDiffApply(t *testing.T) {
	t.Parallel()
	db, conn, writes := schemaRESTFixture(t, `CREATE TABLE items (id INTEGER PRIMARY KEY, value TEXT);
 CREATE INDEX items_value ON items(value);
 CREATE VIEW item_names AS SELECT value FROM items;
 CREATE TRIGGER items_insert AFTER INSERT ON items BEGIN UPDATE items SET value = CASE WHEN new.value IS NULL THEN 'it''s; empty' ELSE new.value END WHERE id = new.id; END;
 INSERT INTO items VALUES (1, 'kept');`)
	path := filepath.Join(t.TempDir(), "schema.sql")
	var out bytes.Buffer
	if err := runSchema(t.Context(), db, "schema pull", path, schemaOptions{}, &out); err != nil {
		t.Fatal(err)
	}
	source, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	desired := strings.Replace(string(source), "value TEXT)", "value TEXT, tag TEXT DEFAULT 'agent')", 1) + "CREATE INDEX items_tag ON items(tag);\n"
	if err := os.WriteFile(path, []byte(desired), 0o600); err != nil {
		t.Fatal(err)
	}
	out.Reset()
	if err := runSchema(t.Context(), db, "schema diff", path, schemaOptions{}, &out); err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(out.String(), `ALTER TABLE "items" ADD COLUMN tag TEXT DEFAULT 'agent';`) || !strings.Contains(out.String(), "CREATE INDEX items_tag ON items(tag);") {
		t.Fatalf("DDL: %s", &out)
	}
	ddl := out.String()
	out.Reset()
	if err := runSchema(t.Context(), db, "schema apply", path, schemaOptions{dryRun: true}, &out); err != nil || out.String() != ddl || writes.Load() != 0 {
		t.Fatalf("dry-run: %s, %v; writes=%d", &out, err, writes.Load())
	}
	out.Reset()
	if err := runSchema(t.Context(), db, "schema apply", path, schemaOptions{}, &jsonOutput{Writer: &out}); err != nil {
		t.Fatal(err)
	}
	var plan schemaPlan
	if err := json.Unmarshal(out.Bytes(), &plan); err != nil || !plan.Applied || plan.Destructive || len(plan.Statements) != 2 {
		t.Fatalf("apply: %s, %v", &out, err)
	}
	var value, tag string
	if err := conn.QueryRowContext(t.Context(), "SELECT value,tag FROM items WHERE id=1").Scan(&value, &tag); err != nil || value != "kept" || tag != "agent" {
		t.Fatalf("data: %q %q %v", value, tag, err)
	}
	out.Reset()
	if err := runSchema(t.Context(), db, "schema diff", path, schemaOptions{}, &jsonOutput{Writer: &out}); err != nil {
		t.Fatal(err)
	}
	if err := json.Unmarshal(out.Bytes(), &plan); err != nil || len(plan.Statements) != 0 {
		t.Fatalf("second diff: %s, %v", &out, err)
	}
}

func TestSchemaApplyRefusesDrop(t *testing.T) {
	t.Parallel()
	db, conn, writes := schemaRESTFixture(t, "CREATE TABLE valuable (id INTEGER PRIMARY KEY, secret TEXT); INSERT INTO valuable VALUES (1, 'kept');")
	path := filepath.Join(t.TempDir(), "schema.sql")
	if err := os.WriteFile(path, []byte("CREATE TABLE valuable (id INTEGER PRIMARY KEY);"), 0o600); err != nil {
		t.Fatal(err)
	}
	var out bytes.Buffer
	err := runSchema(t.Context(), db, "schema apply", path, schemaOptions{}, &out)
	if err == nil || !strings.Contains(err.Error(), "--allow-drop") || writes.Load() != 0 {
		t.Fatalf("drop refusal: %v; writes=%d", err, writes.Load())
	}
	var secret string
	if err := conn.QueryRowContext(t.Context(), "SELECT secret FROM valuable").Scan(&secret); err != nil || secret != "kept" {
		t.Fatalf("data changed: %v", err)
	}
	if err := runSchema(t.Context(), db, "schema apply", path, schemaOptions{allowDrop: true}, &out); err != nil {
		t.Fatal(err)
	}
}
