package main

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"strings"
	"testing"

	v1 "terrascale.tech/sdk/v1"
)

func TestSQLStdinAndModes(t *testing.T) {
	t.Parallel()
	for _, mode := range []string{"relational", "document"} {
		t.Run(mode, func(t *testing.T) {
			t.Parallel()
			const sql = "SELECT\n  9007199254740993 AS n"
			db := exportTestDB(t, func(w http.ResponseWriter, r *http.Request) {
				var body struct {
					SQL        string            `json:"sql"`
					Statements []v1.SQLStatement `json:"statements"`
				}
				if err := json.NewDecoder(r.Body).Decode(&body); err != nil {
					t.Error(err)
				}
				if mode == "relational" {
					if !strings.HasSuffix(r.URL.Path, "/sql/execute") || len(body.Statements) != 1 || body.Statements[0].SQL != sql {
						t.Errorf("request=%s body=%+v", r.URL.Path, body)
					}
					fmt.Fprint(w, `{"results":[{"columns":[{"name":"n","decltype":null}],"rows":[[9007199254740993]],"rows_affected":0,"last_insert_rowid":null}]}`)
				} else {
					if !strings.HasSuffix(r.URL.Path, "/sql") || body.SQL != sql {
						t.Errorf("request=%s body=%+v", r.URL.Path, body)
					}
					fmt.Fprint(w, `{"matched":1,"truncated":false,"rows":[{"id":"01ARZ3NDEKTSV4RRFFQ69G5FD0","value":{"n":9007199254740993}}]}`)
				}
			})
			for _, structured := range []bool{false, true} {
				var buf bytes.Buffer
				var out io.Writer = &buf
				if structured {
					out = &jsonOutput{Writer: out}
				}
				if err := runSQL(t.Context(), db, mode, "-", strings.NewReader(sql), out); err != nil {
					t.Fatal(err)
				}
				if !strings.Contains(buf.String(), "9007199254740993") || !json.Valid(buf.Bytes()) {
					t.Fatalf("output=%s", buf.String())
				}
			}
		})
	}
}

func TestSQLRejectsEmptyOversizedAndUnreadableStdin(t *testing.T) {
	t.Parallel()
	db := exportTestDB(t, func(http.ResponseWriter, *http.Request) { t.Error("unexpected request") })
	for _, text := range []string{"", " \n", strings.Repeat("x", v1.MaxSQLStatementBytes+1)} {
		if err := runSQL(t.Context(), db, "relational", "-", strings.NewReader(text), io.Discard); !errors.Is(err, errUsage) {
			t.Fatalf("invalid stdin error=%v", err)
		}
	}
	reader, writer := io.Pipe()
	writer.CloseWithError(io.ErrUnexpectedEOF)
	if err := runSQL(t.Context(), db, "relational", "-", reader, io.Discard); !errors.Is(err, io.ErrUnexpectedEOF) {
		t.Fatalf("read failure=%v", err)
	}
}

func TestSQLStdinCancellation(t *testing.T) {
	t.Parallel()
	ctx, cancel := context.WithCancel(t.Context())
	defer cancel()
	reader, writer := io.Pipe()
	defer writer.Close()
	db := exportTestDB(t, func(http.ResponseWriter, *http.Request) { t.Error("unexpected request") })
	go func() {
		writer.Write([]byte("SELECT "))
		cancel()
	}()
	if err := runSQL(ctx, db, "relational", "-", reader, io.Discard); !errors.Is(err, context.Canceled) {
		t.Fatalf("cancellation=%v", err)
	}
}

func TestSQLWriterErrors(t *testing.T) {
	t.Parallel()
	db := exportTestDB(t, func(w http.ResponseWriter, r *http.Request) {
		if strings.HasSuffix(r.URL.Path, "/execute") {
			fmt.Fprint(w, `{"results":[{"columns":[],"rows":[],"rows_affected":1,"last_insert_rowid":null}]}`)
		} else {
			fmt.Fprint(w, `{"matched":1,"truncated":false,"rows":[{"id":"01ARZ3NDEKTSV4RRFFQ69G5FD0","value":{"n":1}}]}`)
		}
	})
	for _, mode := range []string{"document", "relational"} {
		if err := runSQL(t.Context(), db, mode, "SELECT 1", nil, exportFailWriter{}); !errors.Is(err, io.ErrClosedPipe) {
			t.Fatalf("mode=%s error=%v", mode, err)
		}
	}
}
