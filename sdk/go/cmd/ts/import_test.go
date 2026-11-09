package main

import (
	"bytes"
	"encoding/json"
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync"
	"testing"

	tb "terrascale.tech/sdk/v1"
)

func TestDbImportTooLargeHalvingAndResume(t *testing.T) {
	t.Parallel()
	var mu sync.Mutex
	var got []string
	calls := 0
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		body, _ := io.ReadAll(r.Body)
		var req struct {
			Statements []struct {
				SQL string `json:"sql"`
			} `json:"statements"`
		}
		if err := json.Unmarshal(body, &req); err != nil {
			t.Errorf("bad request body: %v", err)
		}
		mu.Lock()
		calls++
		n := calls
		for _, st := range req.Statements {
			got = append(got, st.SQL)
		}
		mu.Unlock()
		w.Header().Set("Content-Type", "application/json")
		switch {
		case len(req.Statements) == 4:
			w.WriteHeader(http.StatusRequestEntityTooLarge)
			io.WriteString(w, `{"error":{"code":"too_large","message":"too many rows changed","statement":0}}`)
		case n == 3:
			w.WriteHeader(http.StatusBadRequest)
			io.WriteString(w, `{"error":{"code":"unique_violation","message":"UNIQUE constraint failed","statement":1,"sqlstate":"23505"}}`)
		default:
			w.WriteHeader(http.StatusOK)
			results := strings.Repeat(`{"columns":[],"rows":[],"rows_affected":0,"last_insert_rowid":null},`, len(req.Statements)-1) + `{"columns":[],"rows":[],"rows_affected":0,"last_insert_rowid":null}`
			io.WriteString(w, `{"results":[`+results+`]}`)
		}
	}))
	t.Cleanup(server.Close)
	client, err := tb.New(tb.Config{BaseURL: server.URL, Token: "header.project.signature", HTTPClient: server.Client()})
	if err != nil {
		t.Fatal(err)
	}
	script := "INSERT INTO t VALUES (1);\nINSERT INTO t VALUES (2);\nINSERT INTO t VALUES (3);\nINSERT INTO t VALUES (4);\n"
	var out, errOut bytes.Buffer
	err = dbImport(t.Context(), client.Database(sqlProjectImport, "main"), "-", 1, strings.NewReader(script), &out, &errOut)
	if err == nil || !strings.Contains(err.Error(), "statement 3") || !strings.Contains(err.Error(), "-from 3") {
		t.Fatalf("want resume at statement 3, got %v", err)
	}
	mu.Lock()
	defer mu.Unlock()
	if strings.Join(got, "|") != strings.Join([]string{"INSERT INTO t VALUES (1)", "INSERT INTO t VALUES (2)", "INSERT INTO t VALUES (3)", "INSERT INTO t VALUES (4)", "INSERT INTO t VALUES (1)", "INSERT INTO t VALUES (2)", "INSERT INTO t VALUES (3)", "INSERT INTO t VALUES (4)"}, "|") {
		t.Fatalf("statements out of order or not retried in halves: %q", got)
	}
	if !strings.Contains(errOut.String(), "imported 2/4 statements") {
		t.Fatalf("missing progress: %q", errOut.String())
	}
}

const sqlProjectImport = "01ARZ3NDEKTSV4RRFFQ69G5FAV"
