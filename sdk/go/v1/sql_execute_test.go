package v1_test

import (
	"database/sql"
	"encoding/json"
	"errors"
	"io"
	"math"
	"net/http"
	"net/http/httptest"
	"os"
	"slices"
	"sync/atomic"
	"testing"

	tb "terrascale.tech/sdk/v1"
)

const sqlProject = "01ARZ3NDEKTSV4RRFFQ69G5FAV"

type sqlReply struct {
	status int
	body   string
}

func sqlServer(t *testing.T, status int, reply string, check func(body string)) (*tb.Database, *atomic.Int32) {
	t.Helper()
	return sqlServerSeq(t, check, sqlReply{status, reply})
}

func sqlServerSeq(t *testing.T, check func(body string), replies ...sqlReply) (*tb.Database, *atomic.Int32) {
	t.Helper()
	calls := new(atomic.Int32)
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		i := int(calls.Add(1)) - 1
		if r.Method != http.MethodPost || r.URL.Path != "/v1/projects/"+sqlProject+"/databases/main/sql/execute" || r.Header.Get("Authorization") != "Bearer header.project.signature" {
			t.Errorf("request: %s %s", r.Method, r.URL.Path)
		}
		body, _ := io.ReadAll(r.Body)
		if check != nil {
			check(string(body))
		}
		reply := replies[min(i, len(replies)-1)]
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(reply.status)
		io.WriteString(w, reply.body)
	}))
	t.Cleanup(server.Close)
	c, err := tb.New(tb.Config{BaseURL: server.URL, Token: "header.project.signature", HTTPClient: server.Client()})
	if err != nil {
		t.Fatal(err)
	}
	return c.Database(sqlProject, "main"), calls
}

func TestSQLExecuteQueryAndBatch(t *testing.T) {
	const request = `{"statements":[{"sql":"INSERT INTO people (id, name, avatar) VALUES (?, :name, ?)","args":["01JA0000000000000000000001",{"base64":"AQID"}],"named":{"name":"Ada"}}]}`
	const reply = `{"results":[{"columns":[{"name":"id","decltype":"TEXT"},{"name":"avatar","decltype":"BLOB"},{"name":"n","decltype":null}],"rows":[["01JA0000000000000000000001",{"base64":"AQID"},9007199254740993]],"rows_affected":0,"last_insert_rowid":null}]}`
	d, _ := sqlServer(t, http.StatusOK, reply, nil)
	rows, err := d.Query(t.Context(), "SELECT id, avatar, n FROM people")
	if err != nil || len(rows) != 1 {
		t.Fatalf("rows=%v err=%v", rows, err)
	}
	var id string
	var avatar []byte
	var n int64
	if err := rows[0].Scan(&id, &avatar, &n); err != nil || id != "01JA0000000000000000000001" || string(avatar) != "\x01\x02\x03" || n != 9007199254740993 {
		t.Fatalf("scan id=%s avatar=%v n=%d err=%v", id, avatar, n, err)
	}
	if got, err := rows[0].Int64("n"); err != nil || got != 9007199254740993 || rows[0].IsNull("id") {
		t.Fatalf("n=%d err=%v", got, err)
	}
	type person struct {
		ID     string `json:"id"`
		Avatar []byte `json:"avatar"`
		N      int64  `json:"n"`
	}
	people, err := tb.QueryAs[person](t.Context(), d, "SELECT id, avatar, n FROM people")
	if err != nil || len(people) != 1 || people[0].ID != id || string(people[0].Avatar) != "\x01\x02\x03" || people[0].N != n {
		t.Fatalf("people=%+v err=%v", people, err)
	}

	w, _ := sqlServer(t, http.StatusOK, `{"results":[{"columns":[],"rows":[],"rows_affected":1,"last_insert_rowid":7},{"columns":[{"name":"c","decltype":null}],"rows":[[1]],"rows_affected":0,"last_insert_rowid":7}]}`, func(body string) {
		if body != request[:len(request)-2]+`,{"sql":"SELECT count(*) AS c FROM people"}]}` {
			t.Errorf("body=%s", body)
		}
	})
	out, err := w.Batch(t.Context(), []tb.Statement{
		{SQL: "INSERT INTO people (id, name, avatar) VALUES (?, :name, ?)", Args: []any{"01JA0000000000000000000001", []byte{1, 2, 3}}, Named: map[string]any{":name": "Ada"}},
		{SQL: "SELECT count(*) AS c FROM people"},
	})
	if err != nil || len(out) != 2 || out[0].RowsAffected != 1 || *out[0].LastInsertRowID != 7 || out[1].Rows[0].Values()[0] != int64(1) {
		t.Fatalf("batch=%+v err=%v", out, err)
	}
	e, _ := sqlServer(t, http.StatusOK, `{"results":[{"columns":[],"rows":[],"rows_affected":1,"last_insert_rowid":null}]}`, func(body string) {
		if body != `{"statements":[{"sql":"UPDATE t SET ok = ? WHERE id = :id","args":[1],"named":{"id":"a"}}]}` {
			t.Errorf("body=%s", body)
		}
	})
	if res, err := e.Execute(t.Context(), "UPDATE t SET ok = ? WHERE id = :id", true, sql.Named("id", "a")); err != nil || res.RowsAffected != 1 {
		t.Fatalf("res=%+v err=%v", res, err)
	}
}

func TestSQLErrorsAndConflictResend(t *testing.T) {
	conflict := sqlReply{http.StatusConflict, `{"error":{"code":"serialization_conflict","message":"conflict","statement":0,"sqlstate":"40001"}}`}
	d, calls := sqlServerSeq(t, nil, conflict, conflict, sqlReply{http.StatusOK, `{"results":[{"columns":[],"rows":[],"rows_affected":1,"last_insert_rowid":null}]}`})
	if _, err := d.Execute(t.Context(), "INSERT INTO t VALUES (1)"); err != nil || calls.Load() != 3 {
		t.Fatalf("err=%v calls=%d", err, calls.Load())
	}

	d, calls = sqlServer(t, http.StatusBadRequest, `{"error":{"code":"unique_violation","message":"UNIQUE constraint failed: t.id","statement":1,"sqlstate":"23505"}}`, nil)
	_, err := d.Batch(t.Context(), []tb.Statement{{SQL: "SELECT 1"}, {SQL: "INSERT INTO t VALUES (1)"}})
	var sqlErr *tb.SQLError
	if !errors.As(err, &sqlErr) || sqlErr.Statement != 1 || sqlErr.SQLState != "23505" || sqlErr.Status != http.StatusBadRequest || sqlErr.IsTransient() || !errors.Is(err, tb.ErrUniqueViolation) || calls.Load() != 1 {
		t.Fatalf("err=%#v calls=%d", err, calls.Load())
	}

	d, _ = sqlServer(t, http.StatusOK, `{"results":[{"columns":[{"name":"a","decltype":null}],"rows":[[true]],"rows_affected":0,"last_insert_rowid":null}]}`, nil)
	if _, err := d.Execute(t.Context(), "INSERT INTO t VALUES (1)"); !errors.Is(err, tb.ErrCommitUncertain) {
		t.Fatalf("invalid success answered %v", err)
	}
}

func TestSQLErrorCodesMatchContract(t *testing.T) {
	data, err := os.ReadFile("../contracts/terrabase/sql/error-codes.json")
	if err != nil {
		t.Fatal(err)
	}
	var contract struct{ Codes []struct{ Code string } }
	if err := json.Unmarshal(data, &contract); err != nil {
		t.Fatal(err)
	}
	known := []tb.ErrorCode{tb.ErrSQL, tb.ErrSyntax, tb.ErrUndefinedTable, tb.ErrUndefinedColumn, tb.ErrDuplicateObject,
		tb.ErrPrimaryKeyViolation, tb.ErrUniqueViolation, tb.ErrNotNullViolation, tb.ErrCheckViolation, tb.ErrForeignKeyViolation,
		tb.ErrConstraintViolation, tb.ErrForbiddenScope, tb.ErrSQLSandbox, tb.ErrSerializationConflict, tb.ErrTooLarge, tb.ErrQueryCanceled,
		tb.ErrTransactionRolledBack, tb.ErrTransactionAborted, tb.ErrReadOnlyTransaction, tb.ErrQuotaExceeded, tb.ErrInvalidRequest,
		tb.ErrTransactionState, tb.ErrTenantBusy, tb.ErrUnavailable, tb.ErrUnauthorized, tb.ErrInternalError}
	if len(contract.Codes) != len(known) {
		t.Fatalf("contract has %d codes, SDK %d", len(contract.Codes), len(known))
	}
	for _, c := range contract.Codes {
		if !slices.Contains(known, tb.ErrorCode(c.Code)) {
			t.Errorf("no constant for %s", c.Code)
		}
	}
}

func TestSQLRejectsInvalidRequestsLocally(t *testing.T) {
	d, calls := sqlServer(t, http.StatusOK, `{}`, nil)
	for name, stmts := range map[string][]tb.Statement{
		"empty batch": nil,
		"too many":    make([]tb.Statement, tb.MaxSQLStatements+1),
		"blank sql":   {{SQL: " "}},
		"bad arg":     {{SQL: "SELECT ?", Args: []any{struct{}{}}}},
		"NaN":         {{SQL: "SELECT ?", Args: []any{math.NaN()}}},
	} {
		if _, err := d.Batch(t.Context(), stmts); err == nil {
			t.Errorf("%s: no error", name)
		}
	}
	if calls.Load() != 0 {
		t.Fatalf("calls = %d", calls.Load())
	}
}
