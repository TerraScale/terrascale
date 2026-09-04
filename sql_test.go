package sdk_test

import (
	"reflect"
	"strings"
	"testing"

	sdk "terrascale.tech/sdk"
)

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
		"SELECT * FROM sqlite_master",
	} {
		if err := sdk.ValidateSQL(statement, []any{"value"}); err == nil {
			t.Errorf("ValidateSQL(%q) unexpectedly succeeded", statement)
		}
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
