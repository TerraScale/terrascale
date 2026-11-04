package sdk_test

import (
	"reflect"
	"strings"
	"testing"

	sdk "terrascale.tech/sdk"
)

// Build binds limit and offset after the where parameters.

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

func TestBuilderValuesDoNotPermitSQLInjection(t *testing.T) {
	statement, params, err := sdk.Select("users").Where(sdk.Eq("customer", "x' OR 1=1 --")).OrderBy("name DESC").Build()
	if err != nil {
		t.Fatal(err)
	}
	if strings.Contains(statement, "1=1") || !reflect.DeepEqual(params, []any{"x' OR 1=1 --"}) {
		t.Fatalf("unsafe query: %q %#v", statement, params)
	}
	if _, _, err := sdk.Select("users").OrderBy("name; DROP TABLE users").Build(); err == nil {
		t.Fatal("expected an invalid ORDER BY to be rejected")
	}
}
