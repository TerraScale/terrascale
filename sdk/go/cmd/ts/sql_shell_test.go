package main

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"slices"
	"strings"
	"testing"
)

type shellTestLines struct {
	lines, history, prompts []string
	err                     error
}

func (s *shellTestLines) SetPrompt(prompt string) { s.prompts = append(s.prompts, prompt) }
func (s *shellTestLines) SaveHistory(line string) error {
	s.history = append(s.history, line)
	return nil
}
func (s *shellTestLines) Readline() (string, error) {
	if len(s.lines) == 0 {
		if s.err != nil {
			return "", s.err
		}
		return "", io.EOF
	}
	line := s.lines[0]
	s.lines = s.lines[1:]
	return line, nil
}

func TestSQLShellMultilineHistoryAndModes(t *testing.T) {
	t.Parallel()
	var statements, paths []string
	db := exportTestDB(t, func(w http.ResponseWriter, r *http.Request) {
		var body map[string]json.RawMessage
		json.NewDecoder(r.Body).Decode(&body)
		paths = append(paths, r.URL.Path)
		if strings.HasSuffix(r.URL.Path, "/execute") {
			var batch []struct{ SQL string }
			json.Unmarshal(body["statements"], &batch)
			statements = append(statements, batch[0].SQL)
			fmt.Fprint(w, `{"results":[{"columns":[],"rows":[],"rows_affected":1,"last_insert_rowid":null}]}`)
		} else {
			var sql string
			json.Unmarshal(body["sql"], &sql)
			statements = append(statements, sql)
			fmt.Fprint(w, `{"matched":0,"truncated":false,"rows":[]}`)
		}
	})
	lines := &shellTestLines{lines: []string{"SELECT ';'", "/* ; */", ".run", ".mode document", "SELECT *", "FROM users", ".run", ".quit"}}
	var out, diagnostics bytes.Buffer
	if err := runSQLShell(t.Context(), db, lines, &jsonOutput{Writer: &out}, &diagnostics); err != nil {
		t.Fatal(err)
	}
	want := []string{"SELECT ';'\n/* ; */\n", "SELECT *\nFROM users\n"}
	if !slices.Equal(statements, want) || !slices.Equal(lines.history, want) || len(paths) != 2 || !strings.HasSuffix(paths[0], "/execute") || !strings.HasSuffix(paths[1], "/sql") {
		t.Fatalf("statements=%q history=%q paths=%q", statements, lines.history, paths)
	}
	if strings.Count(out.String(), `"type":"record"`) != 2 || !strings.HasSuffix(out.String(), "{\"type\":\"complete\"}\n") || strings.Contains(out.String(), "History") {
		t.Fatalf("stream=%s", out.String())
	}
	if !slices.Contains(lines.prompts, "... .run> ") || !slices.Contains(lines.prompts, "main[document]> ") || !strings.Contains(diagnostics.String(), "session-only") {
		t.Fatalf("prompts=%q diagnostics=%s", lines.prompts, diagnostics.String())
	}
}

func TestSQLShellIncompleteInputAndReset(t *testing.T) {
	t.Parallel()
	db := exportTestDB(t, func(http.ResponseWriter, *http.Request) { t.Error("unexpected request") })
	for _, reset := range []bool{false, true} {
		lines := &shellTestLines{lines: []string{"SELECT 1"}}
		if reset {
			lines.lines = append(lines.lines, ".reset", ".quit")
		}
		err := runSQLShell(t.Context(), db, lines, io.Discard, io.Discard)
		if reset && err != nil || !reset && !errors.Is(err, io.ErrUnexpectedEOF) {
			t.Fatalf("reset=%t error=%v", reset, err)
		}
	}
}

func TestSQLShellFailureNeverCompletes(t *testing.T) {
	t.Parallel()
	db := exportTestDB(t, func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(http.StatusBadRequest)
		fmt.Fprint(w, `{"error":{"code":"sql_error","message":"invalid SQL"}}`)
	})
	lines := &shellTestLines{lines: []string{"INVALID", ".run", ".quit"}}
	var out bytes.Buffer
	err := runSQLShell(t.Context(), db, lines, &jsonOutput{Writer: &out}, io.Discard)
	if err == nil || out.Len() != 0 {
		t.Fatalf("output=%s error=%v", out.String(), err)
	}
}

func TestSQLShellInterruptionAndDiagnosticsFailure(t *testing.T) {
	t.Parallel()
	db := exportTestDB(t, func(http.ResponseWriter, *http.Request) { t.Error("unexpected request") })
	if err := runSQLShell(t.Context(), db, &shellTestLines{err: context.Canceled}, io.Discard, io.Discard); !errors.Is(err, context.Canceled) {
		t.Fatalf("interruption=%v", err)
	}
	if err := runSQLShell(t.Context(), db, &shellTestLines{}, io.Discard, exportFailWriter{}); !errors.Is(err, io.ErrClosedPipe) {
		t.Fatalf("diagnostics failure=%v", err)
	}
}

func TestSQLShellOutputFailure(t *testing.T) {
	t.Parallel()
	db := exportTestDB(t, func(w http.ResponseWriter, r *http.Request) {
		fmt.Fprint(w, `{"results":[{"columns":[],"rows":[],"rows_affected":1,"last_insert_rowid":null}]}`)
	})
	lines := &shellTestLines{lines: []string{"SELECT 1", ".run", ".quit"}}
	out := &jsonOutput{Writer: exportFailWriter{}}
	if err := runSQLShell(t.Context(), db, lines, out, io.Discard); !errors.Is(err, io.ErrClosedPipe) || !out.failed {
		t.Fatalf("output error=%v failed=%t", err, out.failed)
	}
}
