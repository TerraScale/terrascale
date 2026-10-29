package sdk_test

import (
	"reflect"
	"strings"
	"testing"

	sdk "terrascale.tech/sdk"
)

func benchmarkQueryBuilderSchema() sdk.CollectionSchema {
	fields := []sdk.FieldDef{
		{Name: "tenant", Type: sdk.FieldString, Required: true, Indexed: true},
		{Name: "title", Type: sdk.FieldString, Indexed: true},
		{Name: "body", Type: sdk.FieldText},
		{Name: "visits", Type: sdk.FieldCounter},
		{Name: "score", Type: sdk.FieldFloat, Indexed: true},
		{Name: "tags", Type: sdk.FieldJSON},
		{Name: "expires_at", Type: sdk.FieldTimestamp, Indexed: true},
	}
	return sdk.CollectionSchema{
		Name:                 "events",
		Fields:               fields,
		Indexes:              []sdk.IndexDef{{Name: "events_by_tenant", Fields: []string{"tenant"}, Kind: sdk.IndexBTree}},
		HistoryRetentionDays: sdk.DefaultHistoryRetentionDays,
		MergePolicy:          sdk.MergeRemoveWins,
	}
}

func BenchmarkQueryBuilderBuild(b *testing.B) {
	schema := benchmarkQueryBuilderSchema()
	b.ReportAllocs()
	b.ResetTimer()
	for b.Loop() {
		query, err := sdk.SelectSchema(&schema)
		if err != nil {
			b.Fatal(err)
		}
		query.WhereEq(&schema.Fields[0], "tenant-1").
			WhereGte(&schema.Fields[4], 1.5).
			WhereJSONField(&schema.Fields[5], "$.channel", "=", "web").
			OrderByField(&schema.Fields[6], sdk.Desc).
			Limit(50).
			Offset(100)
		statement, params, err := query.Build()
		if err != nil {
			b.Fatal(err)
		}
		// Build binds limit and offset after the where parameters.
		if statement == "" || len(params) != 5 {
			b.Fatalf("build = %q %+v", statement, params)
		}
	}
}

func TestSchemaSelectBuilderBindsFieldsMatchJSONAndPagination(t *testing.T) {
	name := sdk.FieldDef{Name: "name", Type: sdk.FieldString}
	age := sdk.FieldDef{Name: "age", Type: sdk.FieldInt}
	schema := sdk.CollectionSchema{Name: "users", Fields: []sdk.FieldDef{name, age}}

	query, err := sdk.SelectSchema(&schema)
	if err != nil {
		t.Fatal(err)
	}
	statement, params, err := query.Column(&name).
		WhereGte(&age, 18).
		WhereMatch(&name, "alice").
		WhereJSON("$.profile", "=", "staff").
		OrderByField(&age, sdk.Desc).
		Limit(10).
		Offset(5).
		Build()
	if err != nil {
		t.Fatal(err)
	}
	if want := `SELECT "name" FROM "users" WHERE (("age" >= ?) AND ("name" MATCH ?)) AND (body ->> '$.profile' = ?) ORDER BY "age" DESC LIMIT ? OFFSET ?`; statement != want {
		t.Fatalf("statement = %q, want %q", statement, want)
	}
	if want := []any{18, "alice", "staff", 10, 5}; !reflect.DeepEqual(params, want) {
		t.Fatalf("params = %#v, want %#v", params, want)
	}
}

func TestSchemaSelectBuilderRejectsUndeclaredFieldsAndUnsafeOrder(t *testing.T) {
	name := sdk.FieldDef{Name: "name", Type: sdk.FieldString}
	other := sdk.FieldDef{Name: "other", Type: sdk.FieldString}
	query, err := sdk.SelectSchema(&sdk.CollectionSchema{Name: "users", Fields: []sdk.FieldDef{name}})
	if err != nil {
		t.Fatal(err)
	}
	if _, _, err := query.Column(&other).Build(); err == nil {
		t.Fatal("expected undeclared field to be rejected")
	}

	if _, _, err := sdk.Select("users").OrderBy("age DESC, id").Build(); err == nil {
		t.Fatal("expected ORDER BY expression injection to be rejected")
	}
	if _, _, err := sdk.Select("users").Offset(-1).Build(); err == nil {
		t.Fatal("expected negative offset to be rejected")
	}
}

func TestValidateSQLReadOnlyAllowListAndBindings(t *testing.T) {
	for _, test := range []struct {
		statement string
		params    []any
	}{
		{"SELECT * FROM users WHERE id = ?", []any{"doc-1"}},
		{"SELECT * FROM users WHERE id = $1", []any{"doc-1"}},
		{"WITH recent AS (SELECT * FROM users) SELECT * FROM recent", nil},
		{"EXPLAIN QUERY PLAN SELECT * FROM users", nil},
		{"SELECT * FROM users;", nil},
	} {
		if err := sdk.ValidateSQL(test.statement, test.params); err != nil {
			t.Errorf("ValidateSQL(%q): %v", test.statement, err)
		}
	}

	for _, statement := range []string{
		"INSERT INTO users VALUES (?)",
		"UPDATE users SET name = ?",
		"DELETE FROM users",
		"CREATE TABLE stolen (id INTEGER)",
		"SELECT * FROM users; SELECT * FROM users",
		"SELECT * FROM users -- DROP TABLE users",
		"SELECT random () FROM users",
		"SELECT CURRENT_TIMESTAMP FROM users",
		"WITH RECURSIVE x AS (SELECT * FROM users) SELECT * FROM x",
		"EXPLAIN WITH RECURSIVE x AS (SELECT * FROM users) SELECT * FROM x",
		"EXPLAIN QUERY PLAN WITH RECURSIVE x AS (SELECT * FROM users) SELECT * FROM x",
		"SELECT * FROM users WHERE id IN (WITH RECURSIVE x AS (SELECT id FROM users) SELECT id FROM x)",
		"SELECT * FROM sqlite_master",
	} {
		if err := sdk.ValidateSQL(statement, []any{"value"}); err == nil {
			t.Errorf("ValidateSQL(%q) unexpectedly succeeded", statement)
		}
	}
}

func TestValidateSQLAcceptsNamedParameterObjectBindings(t *testing.T) {
	if err := sdk.ValidateSQL("SELECT * FROM users WHERE id = :id AND state = $state", map[string]any{"id": "doc-1", "state": "open"}); err != nil {
		t.Fatal(err)
	}
	if err := sdk.ValidateSQL("SELECT * FROM users WHERE id = :id", map[string]any{"other": "doc-1"}); err == nil {
		t.Fatal("expected named binding mismatch")
	}
}

func TestValidateSQLChecksNumberedAndAutomaticParameters(t *testing.T) {
	if err := sdk.ValidateSQL("SELECT * FROM users WHERE a = ? AND b = ?", []any{1, 2}); err != nil {
		t.Fatal(err)
	}
	if err := sdk.ValidateSQL("SELECT * FROM users WHERE a = ?1 AND b = ?2", []any{1, 2}); err != nil {
		t.Fatal(err)
	}
	for _, test := range []struct {
		statement string
		params    []any
	}{
		{"SELECT * FROM users WHERE a = ?", nil},
		{"SELECT * FROM users", []any{1}},
		{"SELECT * FROM users WHERE a = $2", []any{1, 2}},
		{"SELECT * FROM users WHERE a = ?0", []any{1}},
		{"SELECT * FROM users WHERE a = :name", []any{"x"}},
	} {
		if err := sdk.ValidateSQL(test.statement, test.params); err == nil {
			t.Errorf("ValidateSQL(%q) unexpectedly succeeded", test.statement)
		}
	}
}

func TestJSONPathAndRawValuesDoNotPermitSQLInjection(t *testing.T) {
	query := sdk.Select("users").JSONEq("$.customer", "x' OR 1=1 --")
	statement, params, err := query.Build()
	if err != nil {
		t.Fatal(err)
	}
	if strings.Contains(statement, "1=1") || !reflect.DeepEqual(params, []any{"x' OR 1=1 --"}) {
		t.Fatalf("unsafe JSON query: %q %#v", statement, params)
	}
	if _, _, err := sdk.Select("users").JSONEq("$.customer-name", "x").Build(); err == nil {
		t.Fatal("expected invalid JSON path to be rejected")
	}
	if statement, _, err := sdk.Select("users").OrderByJSON("$.customer", sdk.Desc).Build(); err != nil {
		t.Fatal(err)
	} else if want := `SELECT * FROM "users" ORDER BY body ->> '$.customer' DESC`; statement != want {
		t.Fatalf("JSON order statement = %q, want %q", statement, want)
	}
}
