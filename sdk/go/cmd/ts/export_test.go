package main

import (
	"bytes"
	"context"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"

	v1 "terrascale.tech/sdk/v1"
)

const cliExportRecord = `{"collection":"users","id":"01ARZ3NDEKTSV4RRFFQ69G5FD0","document":{"n":9007199254740993}}` + "\n"

func exportTestDB(t *testing.T, handler http.HandlerFunc) *v1.Database {
	t.Helper()
	server := httptest.NewServer(handler)
	t.Cleanup(server.Close)
	c, err := v1.New(v1.Config{BaseURL: server.URL, Token: "fixture.jwt.signature"})
	if err != nil {
		t.Fatal(err)
	}
	return c.Database("01ARZ3NDEKTSV4RRFFQ69G5FA8", "main")
}

func TestExportDatabaseOutput(t *testing.T) {
	t.Parallel()
	for _, structured := range []bool{false, true} {
		t.Run(fmt.Sprint(structured), func(t *testing.T) {
			t.Parallel()
			db := exportTestDB(t, func(w http.ResponseWriter, r *http.Request) {
				if !strings.HasSuffix(r.URL.Path, "/export") {
					t.Errorf("path=%s", r.URL.Path)
				}
				w.Header().Set("Content-Type", "application/x-ndjson")
				fmt.Fprint(w, cliExportRecord)
			})
			var buf bytes.Buffer
			var out io.Writer = &buf
			if structured {
				out = &jsonOutput{Writer: out}
			}
			if err := exportDatabase(t.Context(), db, out); err != nil {
				t.Fatal(err)
			}
			want := cliExportRecord
			if structured {
				want = `{"type":"record","data":` + strings.TrimSpace(cliExportRecord) + "}\n" + `{"type":"complete"}` + "\n"
			}
			if buf.String() != want {
				t.Fatalf("output=%s", buf.String())
			}
		})
	}
}

func TestExportDatabaseIncomplete(t *testing.T) {
	t.Parallel()
	db := exportTestDB(t, func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/x-ndjson")
		w.Header().Set("Content-Length", fmt.Sprint(len(cliExportRecord)+10))
		fmt.Fprint(w, cliExportRecord)
	})
	var buf bytes.Buffer
	out := &jsonOutput{Writer: &buf}
	err := exportDatabase(t.Context(), db, out)
	if !errors.Is(err, io.ErrUnexpectedEOF) || !strings.Contains(err.Error(), "export incomplete") || strings.Contains(buf.String(), `"complete"`) {
		t.Fatalf("output=%s error=%v", buf.String(), err)
	}
}

func TestExportDatabaseEmptyCompletion(t *testing.T) {
	t.Parallel()
	db := exportTestDB(t, func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/x-ndjson")
	})
	var buf bytes.Buffer
	if err := exportDatabase(t.Context(), db, &jsonOutput{Writer: &buf}); err != nil || buf.String() != "{\"type\":\"complete\"}\n" {
		t.Fatalf("output=%s error=%v", buf.String(), err)
	}
}

type exportFailWriter struct{}

func (exportFailWriter) Write([]byte) (int, error) { return 0, io.ErrClosedPipe }

func TestExportDatabaseWriterFailureAndCancellation(t *testing.T) {
	t.Parallel()
	ctx, cancel := context.WithCancel(t.Context())
	defer cancel()
	db := exportTestDB(t, func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/x-ndjson")
		fmt.Fprint(w, cliExportRecord)
		w.(http.Flusher).Flush()
		<-r.Context().Done()
	})
	out := &jsonOutput{Writer: exportFailWriter{}}
	if err := exportDatabase(ctx, db, out); !errors.Is(err, io.ErrClosedPipe) || !out.failed {
		t.Fatalf("writer failure=%v failed=%t", err, out.failed)
	}
	cancel()
	if err := exportDatabase(ctx, db, io.Discard); !errors.Is(err, context.Canceled) {
		t.Fatalf("cancellation=%v", err)
	}
}
