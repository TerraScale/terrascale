package main

import (
	"context"
	"database/sql"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"maps"
	"os"
	"slices"
	"strings"

	_ "modernc.org/sqlite" // Offline DDL validation only; application data stays in TerraBase.
	v1 "terrascale.tech/sdk/v1"
)

const schemaSelection = "SELECT type, name, tbl_name, sql FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%' AND sql IS NOT NULL"

type schemaPlan struct {
	Statements  []string `json:"statements"`
	Destructive bool     `json:"destructive"`
	Applied     bool     `json:"applied"`
}

type schemaOptions struct {
	dryRun, allowDrop bool
}

func runSchema(ctx context.Context, db *v1.Database, command, path string, options schemaOptions, out io.Writer) error {
	live, err := schema(ctx, db)
	if err != nil {
		return err
	}
	if command == "schema pull" {
		if path == "" {
			path = "schema.sql"
		}
		var b strings.Builder
		for _, e := range orderedSchema(live) {
			b.WriteString(strings.TrimSuffix(strings.TrimSpace(e.SQL), ";"))
			b.WriteString(";\n\n")
		}
		// Exclusive creation protects a locally edited desired schema.
		f, err := os.OpenFile(path, os.O_WRONLY|os.O_CREATE|os.O_EXCL, 0o644)
		if err != nil {
			return fmt.Errorf("write schema.sql (move an existing file first): %w", err)
		}
		_, writeErr := io.WriteString(f, b.String())
		closeErr := f.Close()
		if err := errors.Join(writeErr, closeErr); err != nil {
			os.Remove(path)
			return err
		}
		if jsonMode(out) {
			return emitJSON(out, struct {
				Path string `json:"path"`
				SQL  string `json:"sql"`
			}{path, b.String()})
		}
		_, err = fmt.Fprintln(out, path)
		return err
	}
	f, err := os.Open(path)
	if err != nil {
		return err
	}
	defer f.Close()
	source, err := io.ReadAll(io.LimitReader(f, v1.MaxSQLRequestBytes+1))
	if err != nil {
		return err
	}
	if len(source) > v1.MaxSQLRequestBytes {
		return fmt.Errorf("schema.sql exceeds 1 MiB")
	}
	desired, err := readDesiredSchema(ctx, string(source))
	if err != nil {
		return err
	}
	plan, err := planSchema(ctx, live, desired)
	if err != nil {
		return err
	}
	if command == "schema apply" && !options.dryRun && len(plan.Statements) > 0 {
		if plan.Destructive && !options.allowDrop {
			return &cliError{code: "schema_drop_refused", message: "schema contains DROP operations; inspect ts schema diff and pass --allow-drop to consent"}
		}
		batch := []v1.Statement{schemaGuard(live)}
		for _, statement := range plan.Statements {
			batch = append(batch, v1.Statement{SQL: statement})
		}
		if _, err := db.Batch(ctx, batch); err != nil {
			return fmt.Errorf("schema apply: %w", err)
		}
		plan.Applied = true
	}
	if jsonMode(out) {
		return emitJSON(out, plan)
	}
	if len(plan.Statements) == 0 {
		_, err = fmt.Fprintln(out, "-- Schema is up to date.")
		return err
	}
	for _, statement := range plan.Statements {
		if _, err = fmt.Fprintln(out, statement+";"); err != nil {
			return err
		}
	}
	if plan.Applied {
		_, err = fmt.Fprintf(out, "-- Applied %d statements in one transaction.\n", len(plan.Statements))
	}
	return err
}

func orderedSchema(entities map[string]schemaEntity) []schemaEntity {
	result := slices.Collect(maps.Values(entities))
	rank := map[string]int{"table": 0, "index": 1, "view": 2, "trigger": 3}
	slices.SortFunc(result, func(a, b schemaEntity) int {
		if rank[a.Kind] != rank[b.Kind] {
			return rank[a.Kind] - rank[b.Kind]
		}
		return strings.Compare(a.Name, b.Name)
	})
	return result
}

// A fresh, private in-memory SQLite connection validates DDL, never user data.
func openSchemaModel(ctx context.Context) (*sql.Conn, func(), error) {
	db, err := sql.Open("sqlite", ":memory:")
	if err != nil {
		return nil, nil, err
	}
	db.SetMaxOpenConns(1)
	conn, err := db.Conn(ctx)
	if err != nil {
		db.Close()
		return nil, nil, err
	}
	closeModel := func() { conn.Close(); db.Close() }
	if _, err = conn.ExecContext(ctx, "PRAGMA foreign_keys = ON"); err != nil {
		closeModel()
		return nil, nil, err
	}
	return conn, closeModel, nil
}

func modelSchema(ctx context.Context, conn *sql.Conn) (map[string]schemaEntity, error) {
	rows, err := conn.QueryContext(ctx, schemaSelection)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	result := map[string]schemaEntity{}
	for rows.Next() {
		var e schemaEntity
		if err := rows.Scan(&e.Kind, &e.Name, &e.Table, &e.SQL); err != nil {
			return nil, err
		}
		result[e.Kind+" "+e.Name] = e
	}
	return result, rows.Err()
}

func readDesiredSchema(ctx context.Context, source string) (map[string]schemaEntity, error) {
	statements, err := schemaStatements(source)
	if err != nil {
		return nil, err
	}
	conn, closeModel, err := openSchemaModel(ctx)
	if err != nil {
		return nil, err
	}
	defer closeModel()
	for _, statement := range statements {
		// CREATE TABLE AS could execute queries rather than declare a schema.
		tokens, _ := schemaTokens(statement)
		if strings.EqualFold(tokens[1].raw, "TABLE") {
			if _, _, err := tableParts(statement); err != nil {
				return nil, err
			}
			for _, t := range tokens[2:] {
				if t.raw == "(" {
					break
				}
				if strings.EqualFold(t.raw, "AS") {
					return nil, fmt.Errorf("CREATE TABLE AS is not a declarative schema")
				}
			}
		}
		if _, err := conn.ExecContext(ctx, statement); err != nil {
			return nil, fmt.Errorf("invalid desired schema: %w", err)
		}
	}
	result, err := modelSchema(ctx, conn)
	if err != nil {
		return nil, err
	}
	for _, e := range result {
		lower := strings.ToLower(e.Name)
		if strings.HasPrefix(lower, "_tb_") || strings.HasPrefix(lower, "_ts_") || strings.HasPrefix(lower, "turso_") {
			return nil, fmt.Errorf("schema uses a reserved object name")
		}
	}
	return result, nil
}

func planSchema(ctx context.Context, live, desired map[string]schemaEntity) (schemaPlan, error) {
	plan := schemaPlan{Statements: []string{}}
	// Drop dependents before tables. Replacements retain their original CREATE SQL.
	old := orderedSchema(live)
	for _, e := range slices.Backward(old) {
		next, exists := desired[e.Kind+" "+e.Name]
		if e.Kind == "table" && exists {
			continue
		}
		if !exists || !sameSchemaSQL(e.SQL, next.SQL) {
			plan.Statements = append(plan.Statements, "DROP "+strings.ToUpper(e.Kind)+" "+quoteSchemaName(e.Name))
			plan.Destructive = true
		}
	}
	for _, e := range orderedSchema(desired) {
		prev, exists := live[e.Kind+" "+e.Name]
		if exists && sameSchemaSQL(prev.SQL, e.SQL) {
			continue
		}
		if e.Kind == "table" && exists {
			statements, err := alterSchemaTable(prev, e)
			if err != nil {
				return plan, err
			}
			for _, s := range statements {
				if strings.Contains(s, " DROP COLUMN ") {
					plan.Destructive = true
				}
			}
			plan.Statements = append(plan.Statements, statements...)
		} else {
			plan.Statements = append(plan.Statements, strings.TrimSuffix(strings.TrimSpace(e.SQL), ";"))
		}
	}
	if len(plan.Statements) > v1.MaxSQLStatements-1 {
		return plan, fmt.Errorf("schema diff exceeds 255 DDL statements; use smaller changes")
	}
	if len(plan.Statements) == 0 {
		return plan, nil
	}
	// Replay against a schema-only copy to catch dependency/order errors before any write.
	conn, closeModel, err := openSchemaModel(ctx)
	if err != nil {
		return plan, err
	}
	defer closeModel()
	for _, e := range old {
		if _, err := conn.ExecContext(ctx, e.SQL); err != nil {
			return plan, fmt.Errorf("cannot model live schema; use ts migrate: %w", err)
		}
	}
	for _, s := range plan.Statements {
		if len(s) > 16384 {
			return plan, fmt.Errorf("DDL statement exceeds 16 KiB")
		}
		if _, err := conn.ExecContext(ctx, s); err != nil {
			return plan, fmt.Errorf("cannot safely plan schema; use ts migrate: %w", err)
		}
	}
	final, err := modelSchema(ctx, conn)
	if err != nil {
		return plan, err
	}
	if len(final) != len(desired) {
		return plan, fmt.Errorf("schema plan changes dependent objects; use ts migrate")
	}
	for key, e := range desired {
		if !sameSchemaSQL(final[key].SQL, e.SQL) {
			return plan, fmt.Errorf("schema plan cannot reproduce %q; use ts migrate", e.Name)
		}
	}
	return plan, nil
}

// Recheck the introspected catalogue inside the same atomic REST batch as the DDL.
// A concurrent schema edit raises a SQL error and rolls back instead of applying
// a stale destructive plan. The public sandbox filters internal objects here too.
func schemaGuard(live map[string]schemaEntity) v1.Statement {
	expected, _ := json.Marshal(orderedSchema(live))
	if len(live) == 0 {
		expected = []byte("[]")
	}
	// Turso supports scalar count subqueries, but not compound EXCEPT inside
	// EXISTS. Unique catalogue names make equal counts plus exact matches a
	// complete comparison, including concurrent additions and replacements.
	count := "SELECT count(*) FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%' AND sql IS NOT NULL"
	matched := "SELECT count(*) FROM sqlite_schema AS s JOIN json_each(?) AS e ON s.type=json_extract(e.value,'$.kind') AND s.name=json_extract(e.value,'$.name') AND s.tbl_name=json_extract(e.value,'$.table') AND s.sql=json_extract(e.value,'$.sql') WHERE s.name NOT LIKE 'sqlite_%' AND s.sql IS NOT NULL"
	return v1.Statement{SQL: "SELECT CASE WHEN (" + count + ")=? AND (" + matched + ")=? THEN 1 ELSE json('schema changed; rerun ts schema diff') END", Args: []any{len(live), string(expected), len(live)}}
}
