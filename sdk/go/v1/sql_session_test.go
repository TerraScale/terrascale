package v1_test

import (
	"bytes"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"net/http/httptest"
	"os"
	"reflect"
	"testing"

	"github.com/coder/websocket"

	tb "terrascale.tech/sdk/v1"
)

func decodeNumbers(t *testing.T, data []byte, v any) {
	t.Helper()
	dec := json.NewDecoder(bytes.NewReader(data))
	dec.UseNumber()
	if err := dec.Decode(v); err != nil {
		t.Errorf("decode %s: %v", data, err)
	}
}

// sqlSocketDatabase serves the recorded session in contracts/terrabase/sql/websocket-session.json.
func sqlSocketDatabase(t *testing.T) *tb.Database {
	t.Helper()
	data, err := os.ReadFile("../contracts/terrabase/sql/websocket-session.json")
	if err != nil {
		t.Fatal(err)
	}
	var fixture struct {
		Exchanges []struct{ Client, Server json.RawMessage }
	}
	decodeNumbers(t, data, &fixture)
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/v1/projects/"+sqlProject+"/databases/main/sql/ws" || r.Header.Get("Authorization") != "Bearer header.project.signature" {
			t.Errorf("upgrade: %s %s", r.URL.Path, r.Header.Get("Authorization"))
		}
		ws, err := websocket.Accept(w, r, &websocket.AcceptOptions{Subprotocols: []string{tb.SQLSubprotocol}})
		if err != nil {
			t.Error(err)
			return
		}
		defer ws.CloseNow()
		for i, exchange := range fixture.Exchanges {
			_, frame, err := ws.Read(r.Context())
			if err != nil {
				t.Errorf("exchange %d: %v", i, err)
				return
			}
			var got, want, reply map[string]any
			decodeNumbers(t, frame, &got)
			decodeNumbers(t, exchange.Client, &want)
			decodeNumbers(t, exchange.Server, &reply)
			reply["id"] = got["id"]
			delete(got, "id")
			if !reflect.DeepEqual(got, want) {
				t.Errorf("exchange %d: client sent %s, want %s", i, frame, exchange.Client)
			}
			out, _ := json.Marshal(reply)
			if err := ws.Write(r.Context(), websocket.MessageText, out); err != nil {
				t.Errorf("exchange %d: %v", i, err)
				return
			}
		}
		if _, _, err := ws.Read(r.Context()); websocket.CloseStatus(err) != websocket.StatusNormalClosure {
			t.Errorf("after the script: %v", err)
		}
	}))
	t.Cleanup(server.Close)
	c, err := tb.New(tb.Config{BaseURL: server.URL, Token: "header.project.signature", HTTPClient: server.Client()})
	if err != nil {
		t.Fatal(err)
	}
	return c.Database(sqlProject, "main")
}

func TestSQLSessionReplaysRecordedSession(t *testing.T) {
	ctx := t.Context()
	s, err := sqlSocketDatabase(t).Connect(ctx)
	if err != nil {
		t.Fatal(err)
	}
	err = s.Transaction(ctx, func(s *tb.SQLSession) error {
		if _, err := s.Execute(ctx, "INSERT INTO ledger VALUES (?, ?)", 2, 25); err != nil {
			return err
		}
		rows, err := s.Query(ctx, "SELECT amount FROM ledger WHERE id = ?", 2)
		if err != nil {
			return err
		}
		if amount, err := rows[0].Int64("amount"); err != nil || amount != 25 || !s.InTransaction() {
			t.Errorf("amount=%d err=%v inTx=%v", amount, err, s.InTransaction())
		}
		return nil
	})
	if err != nil || s.InTransaction() {
		t.Fatalf("commit: err=%v inTx=%v", err, s.InTransaction())
	}

	if err := s.Begin(ctx); err != nil {
		t.Fatal(err)
	}
	var sqlErr *tb.SQLError
	_, err = s.Execute(ctx, "INSERT INTO ledger VALUES (1, 0)")
	if !errors.As(err, &sqlErr) || !errors.Is(err, tb.ErrPrimaryKeyViolation) || sqlErr.SQLState != "23505" || sqlErr.Status != 0 || sqlErr.Statement != 0 {
		t.Fatalf("duplicate key: %v", err)
	}
	if _, err := s.Query(ctx, "SELECT count(*) FROM ledger"); !errors.Is(err, tb.ErrTransactionAborted) {
		t.Fatalf("aborted: %v", err)
	}
	if err := s.Commit(ctx); !errors.Is(err, tb.ErrTransactionRolledBack) || s.InTransaction() {
		t.Fatalf("commit after failure: %v inTx=%v", err, s.InTransaction())
	}

	abort := errors.New("abort")
	err = s.Transaction(ctx, func(s *tb.SQLSession) error {
		if res, err := s.Execute(ctx, "DELETE FROM ledger"); err != nil || res.RowsAffected != 2 {
			t.Errorf("delete: res=%+v err=%v", res, err)
		}
		return abort
	})
	if !errors.Is(err, abort) || s.InTransaction() {
		t.Fatalf("rollback: %v inTx=%v", err, s.InTransaction())
	}

	results, err := s.Batch(ctx, []tb.Statement{{SQL: "SELECT ? AS data, ? AS name", Args: []any{[]byte{1, 2, 3}, "Ada"}}, {SQL: "SELECT count(*) AS n FROM ledger"}})
	if err != nil || len(results) != 2 {
		t.Fatalf("batch: %v", err)
	}
	data, _ := results[0].Rows[0].Bytes("data")
	name, _ := results[0].Rows[0].String("name")
	n, _ := results[1].Rows[0].Int64("n")
	if !bytes.Equal(data, []byte{1, 2, 3}) || name != "Ada" || n != 2 {
		t.Fatalf("batch rows: data=%v name=%q n=%d", data, name, n)
	}
	if err := s.Close(); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Execute(ctx, "SELECT 1"); !errors.Is(err, tb.ErrSQLSessionClosed) {
		t.Fatalf("after close: %v", err)
	}
}

func TestSQLSessionRefusedUpgradeReturnsAuthError(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(http.StatusUnauthorized)
		io.WriteString(w, `{"error":{"code":"invalid_key","message":"the API key is not valid"}}`)
	}))
	t.Cleanup(server.Close)
	c, err := tb.New(tb.Config{BaseURL: server.URL, Token: "header.project.signature", HTTPClient: server.Client()})
	if err != nil {
		t.Fatal(err)
	}
	s, err := c.Database(sqlProject, "main").Connect(t.Context())
	var apiErr *tb.APIError
	if s != nil || !errors.As(err, &apiErr) || apiErr.Status != http.StatusUnauthorized || !errors.Is(err, tb.ErrInvalidKey) {
		t.Fatalf("session=%v err=%v", s, err)
	}
}
