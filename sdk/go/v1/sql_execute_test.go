package v1_test

import (
	"context"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"net/http/httptest"
	"os"
	"strings"
	"testing"

	tb "terrascale.tech/sdk/v1"
)

const sqlProject = "01ARZ3NDEKTSV4RRFFQ69G5FAV"

func sqlServer(t *testing.T, status int, reply string, check func(body map[string]any)) (*tb.Database, *int) {
	t.Helper()
	calls := 0
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		calls++
		if r.Method != http.MethodPost || r.URL.Path != "/v1/projects/"+sqlProject+"/databases/main/sql/execute" || r.Header.Get("Authorization") != "Bearer header.project.signature" {
			t.Errorf("request: %s %s", r.Method, r.URL.Path)
		}
		var body map[string]any
		dec := json.NewDecoder(r.Body)
		dec.UseNumber()
		if err := dec.Decode(&body); err != nil {
			t.Errorf("body: %v", err)
		}
		if check != nil {
			check(body)
		}
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(status)
		io.WriteString(w, reply)
	}))
	t.Cleanup(server.Close)
	c, err := tb.New(tb.Config{BaseURL: server.URL, Token: "header.project.signature", HTTPClient: server.Client()})
	if err != nil {
		t.Fatal(err)
	}
	return c.Database(sqlProject, "main"), &calls
}

func TestExecuteSQLBindsArgsAndDecodesRows(t *testing.T) {
	d, _ := sqlServer(t, http.StatusOK,
		`{"results":[{"columns":[{"name":"id","decltype":"INTEGER"},{"name":"name","decltype":"TEXT"},{"name":"n","decltype":null}],"rows":[[9007199254740993,"Ada",1.5],[2,null,0]],"rows_affected":0,"last_insert_rowid":null}]}`,
		func(body map[string]any) {
			st := body["statements"].([]any)[0].(map[string]any)
			args, _ := json.Marshal(st["args"])
			named, _ := json.Marshal(st["named"])
			if st["sql"] != "SELECT * FROM t WHERE id > ? AND ok = ? AND name = :name" || string(args) != `[1,1]` || string(named) != `{"name":"Ada"}` {
				t.Errorf("statement = %v", st)
			}
		})
	res, err := d.ExecuteSQL(t.Context(), "SELECT * FROM t WHERE id > ? AND ok = ? AND name = :name", 1, true, tb.Named(":name", "Ada"))
	if err != nil {
		t.Fatal(err)
	}
	if len(res.Columns) != 3 || res.Columns[0].Name != "id" || *res.Columns[0].DeclType != "INTEGER" || res.Columns[2].DeclType != nil {
		t.Fatalf("columns = %+v", res.Columns)
	}
	if res.Rows[0][0] != int64(9007199254740993) || res.Rows[0][1] != "Ada" || res.Rows[0][2] != 1.5 || res.Rows[1][1] != nil || res.Rows[1][2] != int64(0) {
		t.Fatalf("rows = %#v", res.Rows)
	}
	if res.LastInsertRowID != nil || res.Maps()[0]["name"] != "Ada" {
		t.Fatalf("result = %+v", res)
	}
}

func TestBatchSQLReturnsEveryResult(t *testing.T) {
	d, _ := sqlServer(t, http.StatusOK,
		`{"results":[{"columns":[],"rows":[],"rows_affected":1,"last_insert_rowid":7},{"columns":[{"name":"c","decltype":null}],"rows":[[1]],"rows_affected":0,"last_insert_rowid":7}]}`,
		func(body map[string]any) {
			if n := len(body["statements"].([]any)); n != 2 {
				t.Errorf("statements = %d", n)
			}
		})
	out, err := d.BatchSQL(t.Context(), []tb.SQLStatement{
		{SQL: "INSERT INTO t(name) VALUES (@name)", Named: map[string]any{"name": "Ada"}},
		{SQL: "SELECT count(*) AS c FROM t"},
	})
	if err != nil || len(out) != 2 || out[0].RowsAffected != 1 || *out[0].LastInsertRowID != 7 || out[1].Rows[0][0] != int64(1) {
		t.Fatalf("batch = %+v, %v", out, err)
	}
}

func TestSQLErrorKeepsStatementAndAPIError(t *testing.T) {
	d, calls := sqlServer(t, http.StatusConflict,
		`{"error":{"code":"sql_error","message":"UNIQUE constraint failed: t.id","statement":1,"sqlite_code":"SQLITE_CONSTRAINT_PRIMARYKEY"}}`, nil)
	_, err := d.BatchSQL(t.Context(), []tb.SQLStatement{{SQL: "SELECT 1"}, {SQL: "INSERT INTO t VALUES (1)"}})
	var sqlErr *tb.SQLError
	if !errors.As(err, &sqlErr) || sqlErr.Code != tb.ErrSQL || *sqlErr.Statement != 1 || sqlErr.SQLiteCode != "SQLITE_CONSTRAINT_PRIMARYKEY" {
		t.Fatalf("err = %#v", err)
	}
	var apiErr *tb.APIError
	if !errors.As(err, &apiErr) || apiErr.Status != http.StatusConflict || !errors.Is(err, tb.ErrSQL) || *calls != 1 {
		t.Fatalf("api err = %v, calls = %d", err, *calls)
	}
	if !strings.Contains(err.Error(), "(statement 1) [SQLITE_CONSTRAINT_PRIMARYKEY]") {
		t.Fatalf("message = %q", err.Error())
	}
}

func TestSQLGenericErrorsStayAPIErrors(t *testing.T) {
	d, _ := sqlServer(t, http.StatusNotFound, `{"error":{"code":"unknown_database","message":"no such database"}}`, nil)
	_, err := d.ExecuteSQL(t.Context(), "SELECT 1")
	var sqlErr *tb.SQLError
	if errors.As(err, &sqlErr) || !errors.Is(err, tb.ErrUnknownDatabase) {
		t.Fatalf("err = %#v", err)
	}
}

func TestSQLRejectsInvalidRequestsLocally(t *testing.T) {
	d, calls := sqlServer(t, http.StatusOK, `{}`, nil)
	for name, stmts := range map[string][]tb.SQLStatement{
		"empty batch": nil,
		"too many":    make([]tb.SQLStatement, tb.MaxSQLStatements+1),
		"blank sql":   {{SQL: " "}},
		"too long":    {{SQL: strings.Repeat("x", tb.MaxSQLStatementBytes+1)}},
		"bad arg":     {{SQL: "SELECT ?", Args: []any{struct{}{}}}},
	} {
		if _, err := d.BatchSQL(t.Context(), stmts); err == nil {
			t.Errorf("%s: no error", name)
		}
	}
	if *calls != 0 {
		t.Fatalf("calls = %d", *calls)
	}
}

// TestSQLLive runs against a real TerraBase when TERRASCALE_SQL_LIVE_TOKEN,
// TERRASCALE_SQL_LIVE_PROJECT and TERRASCALE_SQL_LIVE_DATABASE are set
// against the production API. The token needs write access.
func TestSQLLive(t *testing.T) {
	token, project, database := os.Getenv("TERRASCALE_SQL_LIVE_TOKEN"), os.Getenv("TERRASCALE_SQL_LIVE_PROJECT"), os.Getenv("TERRASCALE_SQL_LIVE_DATABASE")
	if token == "" || project == "" || database == "" {
		t.Skip("set TERRASCALE_SQL_LIVE_TOKEN, TERRASCALE_SQL_LIVE_PROJECT and TERRASCALE_SQL_LIVE_DATABASE to run against TerraBase")
	}
	c, err := tb.New(tb.Config{Token: token})
	if err != nil {
		t.Fatal(err)
	}
	d := c.Database(project, database)
	table := "sdk_go_" + strings.ToLower(tb.NewID())
	out, err := d.BatchSQL(t.Context(), []tb.SQLStatement{
		{SQL: "CREATE TABLE " + table + " (id INTEGER PRIMARY KEY, name TEXT)"},
		{SQL: "INSERT INTO " + table + " (id, name) VALUES (?, :name)", Args: []any{9007199254740993}, Named: map[string]any{"name": "Ada"}},
	})
	if err != nil || out[1].RowsAffected != 1 {
		t.Fatalf("create/insert = %+v, %v", out, err)
	}
	t.Cleanup(func() { _, _ = d.ExecuteSQL(context.Background(), "DROP TABLE "+table) })
	rows, err := d.QuerySQL(t.Context(), "SELECT id, name FROM "+table+" WHERE name = ?", "Ada")
	if err != nil || len(rows) != 1 || rows[0]["id"] != int64(9007199254740993) {
		t.Fatalf("query = %v, %v", rows, err)
	}
	_, err = d.ExecuteSQL(t.Context(), "INSERT INTO "+table+" (id, name) VALUES (?, ?)", 9007199254740993, "dup")
	var sqlErr *tb.SQLError
	if !errors.As(err, &sqlErr) || sqlErr.Statement == nil || *sqlErr.Statement != 0 {
		t.Fatalf("duplicate = %v", err)
	}
}
