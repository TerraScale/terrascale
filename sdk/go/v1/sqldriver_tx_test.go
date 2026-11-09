package v1_test

import (
	"database/sql"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync"
	"testing"

	"github.com/coder/websocket"

	tb "terrascale.tech/sdk/v1"
)

type sqlTxFake struct {
	mu        sync.Mutex
	sockets   [][]string
	committed int
}

func (f *sqlTxFake) handler() http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		ws, err := websocket.Accept(w, r, &websocket.AcceptOptions{Subprotocols: []string{tb.SQLSubprotocol}})
		if err != nil {
			return
		}
		defer ws.CloseNow()
		f.mu.Lock()
		f.sockets = append(f.sockets, nil)
		socket := len(f.sockets) - 1
		f.mu.Unlock()
		for {
			var frame struct {
				ID         int64             `json:"id"`
				Type       string            `json:"type"`
				Statements []json.RawMessage `json:"statements"`
			}
			if _, data, err := ws.Read(r.Context()); err != nil || json.Unmarshal(data, &frame) != nil {
				return
			}
			var trace string
			switch frame.Type {
			case "begin", "rollback":
				trace = frame.Type
			case "commit":
				trace = frame.Type
				f.mu.Lock()
				f.committed++
				f.mu.Unlock()
			case "execute":
				var parsed struct {
					SQL string `json:"sql"`
				}
				json.Unmarshal(frame.Statements[0], &parsed)
				trace = "execute:" + parsed.SQL
			}
			f.mu.Lock()
			f.sockets[socket] = append(f.sockets[socket], trace)
			f.mu.Unlock()
			reply := map[string]any{"id": frame.ID, "type": "result", "results": []any{}, "transaction": frame.Type == "begin" || frame.Type == "execute"}
			if frame.Type == "execute" {
				reply["results"] = []any{map[string]any{"columns": []any{}, "rows": []any{}, "rows_affected": 1, "last_insert_rowid": nil}}
			}
			out, _ := json.Marshal(reply)
			if ws.Write(r.Context(), websocket.MessageText, out) != nil {
				return
			}
		}
	}
}

func TestOpenDBTransactions(t *testing.T) {
	fake := &sqlTxFake{}
	server := httptest.NewServer(fake.handler())
	t.Cleanup(server.Close)
	c, err := tb.New(tb.Config{BaseURL: server.URL, Token: "header.project.signature", HTTPClient: server.Client()})
	if err != nil {
		t.Fatal(err)
	}
	db := tb.OpenDB(c.Database(sqlProject, "main"))
	defer db.Close()
	ctx := t.Context()

	tx, err := db.BeginTx(ctx, nil)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := tx.Exec("INSERT INTO people (id, name) VALUES (?, ?)", "01JA0000000000000000000001", "Ada"); err != nil {
		t.Fatal(err)
	}
	if err := tx.Rollback(); err != nil {
		t.Fatal(err)
	}

	tx, err = db.BeginTx(ctx, nil)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := tx.Exec("INSERT INTO people (id, name) VALUES (?, ?)", "01JA0000000000000000000002", "Grace"); err != nil {
		t.Fatal(err)
	}
	if err := tx.Commit(); err != nil {
		t.Fatal(err)
	}

	if _, err := db.BeginTx(ctx, &sql.TxOptions{Isolation: sql.LevelReadCommitted}); err == nil || !strings.Contains(err.Error(), "isolation") {
		t.Fatalf("begin read committed: err=%v", err)
	}

	fake.mu.Lock()
	defer fake.mu.Unlock()
	if len(fake.sockets) != 2 {
		t.Fatalf("sockets=%d want 2", len(fake.sockets))
	}
	if got := strings.Join(fake.sockets[0], ","); got != "begin,execute:INSERT INTO people (id, name) VALUES (?, ?),rollback" {
		t.Fatalf("socket 0 frames: %s", got)
	}
	if got := strings.Join(fake.sockets[1], ","); got != "begin,execute:INSERT INTO people (id, name) VALUES (?, ?),commit" {
		t.Fatalf("socket 1 frames: %s", got)
	}
	if fake.committed != 1 {
		t.Fatalf("commits=%d want 1", fake.committed)
	}
}
