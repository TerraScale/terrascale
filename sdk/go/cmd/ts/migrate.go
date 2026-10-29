package main

import (
	"cmp"
	"context"
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"fmt"
	"io"
	"maps"
	"os"
	"path/filepath"
	"reflect"
	"regexp"
	"slices"
	"strconv"
	"strings"
	"text/tabwriter"

	v1 "terrascale.tech/sdk/v1"
)

// migrationsDir holds the ordered SQL files ts migrate applies, relative to
// the working directory.
const migrationsDir = "migrations"

// historyTable records each applied migration in the database itself.
const historyTable = "ts_migrations"

var migrationFile = regexp.MustCompile(`^([0-9]+)_[a-z0-9_]+\.sql$`)

type migration struct {
	name, checksum, sql string
}

// migrations reads migrationsDir in version order.
func migrations() ([]migration, error) { return readMigrations(migrationsDir) }

func readMigrations(dir string) ([]migration, error) {
	entries, err := os.ReadDir(dir)
	if errors.Is(err, os.ErrNotExist) {
		return nil, nil
	}
	if err != nil {
		return nil, err
	}
	var out []migration
	for _, e := range entries {
		if e.IsDir() || !migrationFile.MatchString(e.Name()) {
			continue
		}
		data, err := os.ReadFile(filepath.Join(dir, e.Name()))
		if err != nil {
			return nil, err
		}
		sum := sha256.Sum256(data)
		out = append(out, migration{e.Name(), hex.EncodeToString(sum[:]), string(data)})
	}
	slices.SortFunc(out, func(a, b migration) int { return cmp.Compare(migrationVersion(a.name), migrationVersion(b.name)) })
	for i := 1; i < len(out); i++ {
		if migrationVersion(out[i].name) == migrationVersion(out[i-1].name) {
			return nil, fmt.Errorf("%s and %s share version %d", out[i-1].name, out[i].name, migrationVersion(out[i].name))
		}
	}
	return out, nil
}

func migrationVersion(name string) uint64 {
	n, _ := strconv.ParseUint(migrationFile.FindStringSubmatch(name)[1], 10, 64)
	return n
}

// newMigration writes the next empty migration file.
func newMigration(name string, out io.Writer) error { return newMigrationIn(migrationsDir, name, out) }

func newMigrationIn(dir, name string, out io.Writer) error {
	slug := strings.Trim(regexp.MustCompile(`[^a-z0-9]+`).ReplaceAllString(strings.ToLower(name), "_"), "_")
	if slug == "" {
		return errors.New("migration name needs a letter or digit")
	}
	ms, err := readMigrations(dir)
	if err != nil {
		return err
	}
	next := uint64(1)
	if len(ms) > 0 {
		next = migrationVersion(ms[len(ms)-1].name) + 1
	}
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return err
	}
	path := filepath.Join(dir, fmt.Sprintf("%04d_%s.sql", next, slug))
	f, err := os.OpenFile(path, os.O_WRONLY|os.O_CREATE|os.O_EXCL, 0o644)
	if err != nil {
		return err
	}
	if _, err := fmt.Fprintf(f, "-- %s\n", name); err != nil {
		f.Close()
		return err
	}
	if err := f.Close(); err != nil {
		return err
	}
	if jsonMode(out) {
		return emitJSON(out, struct {
			Path string `json:"path"`
		}{path})
	}
	_, err = fmt.Fprintln(out, path)
	return err
}

// applied returns the checksum of each migration the database recorded.
func applied(ctx context.Context, db *v1.Database) (map[string]string, error) {
	rows, err := db.QuerySQL(ctx, "SELECT name, checksum FROM "+historyTable)
	if noTable(err) {
		return map[string]string{}, nil
	}
	if err != nil {
		return nil, err
	}
	out := make(map[string]string, len(rows))
	for _, r := range rows {
		name, _ := r["name"].(string)
		sum, _ := r["checksum"].(string)
		out[name] = sum
	}
	return out, nil
}

// noTable reports TerraBase's answer for a missing table, also given for a
// collection without one.
func noTable(err error) bool {
	sqlErr := (*v1.SQLError)(nil)
	return errors.As(err, &sqlErr) && strings.Contains(sqlErr.Message, "no such table")
}

// migrateUp applies each pending migration in its own transaction together
// with its history row. Earlier migrations can remain committed if a later one fails.
func migrateUp(ctx context.Context, db *v1.Database, out io.Writer) error {
	ms, err := migrations()
	if err != nil {
		return err
	}
	return applyMigrations(ctx, db, ms, out)
}

func applyMigrations(ctx context.Context, db *v1.Database, ms []migration, out io.Writer) error {
	items := []string{}
	if _, err := db.ExecuteSQL(ctx, "CREATE TABLE IF NOT EXISTS "+historyTable+
		" (name TEXT PRIMARY KEY, checksum TEXT NOT NULL, applied_at TEXT NOT NULL)"); err != nil {
		return err
	}
	done, err := applied(ctx, db)
	if err != nil {
		return err
	}
	n := 0
	for _, m := range ms {
		if sum, ok := done[m.name]; ok {
			if sum != m.checksum {
				return fmt.Errorf("%s changed after it was applied to %s: add a new migration instead", m.name, db.Name())
			}
			continue
		}
		var batch []v1.SQLStatement
		for _, st := range splitSQL(m.sql) {
			batch = append(batch, v1.SQLStatement{SQL: st})
		}
		batch = append(batch, v1.SQLStatement{
			SQL:  "INSERT INTO " + historyTable + " (name, checksum, applied_at) VALUES (?, ?, strftime('%Y-%m-%dT%H:%M:%SZ', 'now'))",
			Args: []any{m.name, m.checksum},
		})
		if _, err := db.BatchSQL(ctx, batch); err != nil {
			return fmt.Errorf("%s: %w", m.name, err)
		}
		if jsonMode(out) {
			items = append(items, m.name)
		} else {
			fmt.Fprintf(out, "Applied %s\n", m.name)
		}
		n++
	}
	if jsonMode(out) {
		return emitJSON(out, struct {
			Database string   `json:"database"`
			Applied  []string `json:"applied"`
		}{db.Name(), items})
	}
	if n == 0 {
		fmt.Fprintf(out, "%s is up to date\n", db.Name())
	}
	return nil
}

func migrateStatus(ctx context.Context, db *v1.Database, out io.Writer) error {
	ms, err := migrations()
	if err != nil {
		return err
	}
	return migrationStatus(ctx, db, ms, out)
}

func migrationStatus(ctx context.Context, db *v1.Database, ms []migration, out io.Writer) error {
	type entry struct {
		Name  string `json:"name"`
		State string `json:"state"`
	}
	items := []entry{}
	done, err := applied(ctx, db)
	if err != nil {
		return err
	}
	w := tabwriter.NewWriter(out, 0, 0, 2, ' ', 0)
	if !jsonMode(out) {
		fmt.Fprintln(w, "MIGRATION\tSTATE")
	}
	for _, m := range ms {
		state := "pending"
		if sum, ok := done[m.name]; ok {
			state = "applied"
			if sum != m.checksum {
				state = "changed"
			}
			delete(done, m.name)
		}
		if jsonMode(out) {
			items = append(items, entry{m.name, state})
		} else {
			fmt.Fprintf(w, "%s\t%s\n", m.name, state)
		}
	}
	for _, name := range slices.Sorted(maps.Keys(done)) {
		if jsonMode(out) {
			items = append(items, entry{name, "applied_missing_file"})
		} else {
			fmt.Fprintf(w, "%s\tapplied, file missing\n", name)
		}
	}
	if jsonMode(out) {
		return emitJSON(out, items)
	}
	return w.Flush()
}

// schema describes each table with its declared column types, each
// collection without a SQL table and each index, keyed by kind and name.
// TerraBase reserves sqlite_schema, so it reads the collection and index
// lists and each table's result columns instead.
type schemaEntity struct {
	Kind       string         `json:"kind"`
	Name       string         `json:"name"`
	Columns    []v1.SQLColumn `json:"columns,omitempty"`
	Collection string         `json:"collection,omitempty"`
	Fields     []string       `json:"fields,omitempty"`
}

func schema(ctx context.Context, db *v1.Database) (map[string]schemaEntity, error) {
	out := map[string]schemaEntity{}
	for cursor := ""; ; {
		page, err := db.Collections(ctx, v1.ListOptions{Limit: 200, Cursor: cursor})
		if err != nil {
			return nil, fmt.Errorf("%s: %w", db.Name(), err)
		}
		for _, c := range page.Items {
			res, err := db.ExecuteSQL(ctx, `SELECT * FROM "`+c.Name+`" LIMIT 0`)
			if noTable(err) {
				out["collection "+c.Name] = schemaEntity{Kind: "collection", Name: c.Name}
				continue
			}
			if err != nil {
				return nil, fmt.Errorf("%s: %s: %w", db.Name(), c.Name, err)
			}
			out["table "+c.Name] = schemaEntity{Kind: "table", Name: c.Name, Columns: res.Columns}
		}
		if cursor = page.NextCursor; cursor == "" {
			break
		}
	}
	indexes, err := db.Indexes(ctx)
	if err != nil {
		return nil, fmt.Errorf("%s: %w", db.Name(), err)
	}
	for _, ix := range indexes {
		out["index "+ix.Name] = schemaEntity{Kind: "index", Name: ix.Name, Collection: ix.Collection, Fields: ix.Fields}
	}
	return out, nil
}

// migrateDiff prints the tables, collections and indexes branch adds (+),
// drops (-) or changes against base, its parent when base is empty.
func (s *session) migrateDiff(ctx context.Context, branch, base string, out io.Writer) error {
	if base == "" {
		m, err := s.tb.Database(s.project, branch).Info(ctx)
		if err != nil {
			return err
		}
		if m.BranchedFrom == nil {
			return fmt.Errorf("%s is not a branch: name the database to compare with", branch)
		}
		for db, err := range pages(func(after string) (*v1.Page[v1.ManagedDatabase], error) {
			return s.tb.Databases(ctx, s.project, v1.PageOptions{Limit: 100, After: after})
		}) {
			if err != nil {
				return err
			}
			if db.ID == *m.BranchedFrom {
				base = db.Name
				break
			}
		}
		if base == "" {
			return fmt.Errorf("%s's parent database no longer exists", branch)
		}
	}
	from, err := schema(ctx, s.tb.Database(s.project, base))
	if err != nil {
		return err
	}
	to, err := schema(ctx, s.tb.Database(s.project, branch))
	if err != nil {
		return err
	}
	if jsonMode(out) {
		return emitJSON(out, struct {
			Base    string         `json:"base"`
			Branch  string         `json:"branch"`
			Changes []schemaChange `json:"changes"`
		}{base, branch, schemaChanges(from, to)})
	}
	_, err = io.WriteString(out, schemaDiff(base, branch, schemaDescriptions(from), schemaDescriptions(to)))
	return err
}

type schemaChange struct {
	Kind   string        `json:"kind"`
	Name   string        `json:"name"`
	Before *schemaEntity `json:"before"`
	After  *schemaEntity `json:"after"`
}

func schemaChanges(from, to map[string]schemaEntity) []schemaChange {
	keys := map[string]struct{}{}
	for k := range from {
		keys[k] = struct{}{}
	}
	for k := range to {
		keys[k] = struct{}{}
	}
	changes := []schemaChange{}
	for _, k := range slices.Sorted(maps.Keys(keys)) {
		old, inFrom := from[k]
		cur, inTo := to[k]
		if inFrom && inTo && reflect.DeepEqual(old, cur) {
			continue
		}
		change := schemaChange{Kind: cmp.Or(cur.Kind, old.Kind), Name: cmp.Or(cur.Name, old.Name)}
		if inFrom {
			change.Before = &old
		}
		if inTo {
			change.After = &cur
		}
		changes = append(changes, change)
	}
	return changes
}

// Human output is rendered from the same typed SDK metadata as JSON.
func schemaDescriptions(items map[string]schemaEntity) map[string]string {
	out := map[string]string{}
	for key, item := range items {
		switch item.Kind {
		case "collection":
			out[key] = "collection " + item.Name
		case "index":
			out[key] = fmt.Sprintf("index %s ON %s (%s)", item.Name, item.Collection, strings.Join(item.Fields, ", "))
		case "table":
			var cols []string
			for _, col := range item.Columns {
				if col.DeclType != nil {
					cols = append(cols, strings.TrimSpace(col.Name+" "+*col.DeclType))
				}
			}
			out[key] = fmt.Sprintf("table %s (%s)", item.Name, strings.Join(cols, ", "))
		}
	}
	return out
}

func schemaDiff(base, branch string, from, to map[string]string) string {
	var keys []string
	for k := range from {
		keys = append(keys, k)
	}
	for k := range to {
		if _, ok := from[k]; !ok {
			keys = append(keys, k)
		}
	}
	slices.Sort(keys)
	var b strings.Builder
	for _, k := range keys {
		old, inFrom := from[k]
		cur, inTo := to[k]
		switch {
		case !inFrom:
			fmt.Fprintf(&b, "+ %s\n", cur)
		case !inTo:
			fmt.Fprintf(&b, "- %s\n", old)
		case old != cur:
			fmt.Fprintf(&b, "- %s\n+ %s\n", old, cur)
		}
	}
	if b.Len() == 0 {
		return fmt.Sprintf("%s has the schema of %s\n", branch, base)
	}
	return fmt.Sprintf("--- %s\n+++ %s\n", base, branch) + b.String()
}

// splitSQL splits a script into statements at semicolons outside quotes and
// comments, dropping statements without code.
func splitSQL(script string) []string {
	var out []string
	start, code := 0, false
	for i := 0; i < len(script); i++ {
		c := script[i]
		switch {
		case c == '\'' || c == '"' || c == '`' || c == '[':
			end := c
			if c == '[' {
				end = ']'
			}
			code = true
			if j := strings.IndexByte(script[i+1:], end); j < 0 {
				i = len(script)
			} else {
				i += j + 1
			}
		case strings.HasPrefix(script[i:], "--"):
			if j := strings.IndexByte(script[i:], '\n'); j < 0 {
				i = len(script)
			} else {
				i += j
			}
		case strings.HasPrefix(script[i:], "/*"):
			if j := strings.Index(script[i+2:], "*/"); j < 0 {
				i = len(script)
			} else {
				i += j + 3
			}
		case c == ';':
			if code {
				out = append(out, strings.TrimSpace(script[start:i]))
			}
			start, code = i+1, false
		case c != ' ' && c != '\t' && c != '\n' && c != '\r':
			code = true
		}
	}
	if code {
		out = append(out, strings.TrimSpace(script[start:]))
	}
	return out
}
