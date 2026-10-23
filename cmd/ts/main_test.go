package main

import (
	"bytes"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"slices"
	"strings"
	"sync"
	"testing"

	v1 "terrascale.tech/sdk/v1"
)

func TestPagesFollowsNextAndStopsOnError(t *testing.T) {
	boom := errors.New("boom")
	var got []string
	for item, err := range pages(func(after string) (*v1.Page[string], error) {
		switch after {
		case "":
			return &v1.Page[string]{Items: []string{"a", "b"}, Next: "2"}, nil
		case "2":
			return &v1.Page[string]{Items: []string{"c"}, Next: "3"}, nil
		}
		return nil, boom
	}) {
		if err != nil {
			if !errors.Is(err, boom) {
				t.Fatal(err)
			}
			break
		}
		got = append(got, item)
	}
	if !slices.Equal(got, []string{"a", "b", "c"}) {
		t.Fatalf("pages = %v", got)
	}
}

func TestRunRejectsUnknownCommandsOffline(t *testing.T) {
	// Operand counts are checked before sign-in, so none of these reaches the network.
	for _, args := range [][]string{nil, {"auth"}, {"db"}, {"-nope"}, {"version", "extra"}, {"--version", "extra"},
		{"db", "list", "extra"}, {"db", "create"}, {"doc", "put", "db", "col"}, {"doc", "put", "db", "col", "id", "{}", "x"},
		{"keys", "create"}, {"whoami", "x"}, {"frob"}, {"-project", "p"}, {"db", "regions", "app"},
		{"backup", "restore", "app", "id"}, {"projects", "create"}, {"usage", "a", "b"}, {"completion"},
		{"index", "create", "app", "col"}, {"index", "create", "app", "col", "a", "b", "c", "d", "e"},
		{"schema", "set", "app", "col"}, {"ttl", "set", "app", "col"}, {"import", "app", "sqlite"},
		{"import", "status"}, {"backup", "restore-at", "app", "2026-10-23T00:00:00Z"}, {"db", "changes"}} {
		if err := run(t.Context(), args, io.Discard); !errors.Is(err, errUsage) {
			t.Errorf("run(%q) = %v, want usage", args, err)
		}
	}
}

func TestVersionOffline(t *testing.T) {
	for _, arg := range []string{"version", "--version"} {
		var out bytes.Buffer
		if err := run(t.Context(), []string{arg}, &out); err != nil {
			t.Fatal(err)
		}
		if got, want := out.String(), "ts "+version+"\n"; got != want {
			t.Fatalf("version = %q, want %q", got, want)
		}
	}
}

func TestHelpOffline(t *testing.T) {
	for _, args := range [][]string{{"help"}, {"-h"}, {"--help"}, {"db", "--help"}, {"-project", "p", "doc", "put", "-help"}} {
		var out bytes.Buffer
		if err := run(t.Context(), args, &out); err != nil {
			t.Fatalf("run(%q) = %v", args, err)
		}
		if out.String() != usage {
			t.Fatalf("run(%q) printed %q", args, out.String())
		}
	}
}

func TestImportDocumentsPutsEachLine(t *testing.T) {
	var mu sync.Mutex
	var puts []string
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		body, _ := io.ReadAll(r.Body)
		mu.Lock()
		puts = append(puts, r.Method+" "+r.URL.Query().Get("id")+" "+string(body))
		mu.Unlock()
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(http.StatusCreated)
		io.WriteString(w, `{"id":"`+r.URL.Query().Get("id")+`","version":"tbv1-1","offset":"1","durability":"local"}`)
	}))
	defer srv.Close()
	c, err := v1.New(v1.Config{BaseURL: srv.URL, Token: "tb_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"})
	if err != nil {
		t.Fatal(err)
	}
	col := c.Database("01M5N8CG630T6P449ES15GNZ8N", "main").Collection("people")
	in := `{"id":"01M5N9000000000000000000AA","version":"v","state":"value","document":{"name":"bo"}}

{"id":"01M5N9000000000000000000AB","document":{"name":"ana"}}
`
	var out bytes.Buffer
	if err := importDocuments(t.Context(), col, strings.NewReader(in), &out); err != nil {
		t.Fatal(err)
	}
	want := []string{`PUT 01M5N9000000000000000000AA {"name":"bo"}`, `PUT 01M5N9000000000000000000AB {"name":"ana"}`}
	if !slices.Equal(puts, want) || out.String() != "Imported 2 documents\n" {
		t.Fatalf("puts = %q, out = %q", puts, out.String())
	}
	err = importDocuments(t.Context(), col, strings.NewReader("{\"id\":\"01M5N9000000000000000000AC\"}\n"), io.Discard)
	if err == nil || !strings.Contains(err.Error(), "line 1") {
		t.Fatalf("missing document = %v", err)
	}
}

func TestImportFileStagesConfirmsAndWaits(t *testing.T) {
	const project, id = "01M5N8CG630T6P449ES15GNZ8N", "01M5N9000000000000000000AA"
	src := []byte("CREATE TABLE t (id int);\n")
	var mu sync.Mutex
	var calls []string
	state, received := "awaiting_source", 0
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		mu.Lock()
		defer mu.Unlock()
		call := r.Method + " " + strings.TrimPrefix(r.URL.Path, "/v1/projects/"+project+"/databases")
		calls = append(calls, call)
		w.Header().Set("Content-Type", "application/json")
		switch call {
		case "POST ":
			w.WriteHeader(http.StatusCreated)
			io.WriteString(w, `{"id":"`+id+`","projectId":"`+project+`","name":"app"}`)
			return
		case "PUT /app/import/source":
			body, _ := io.ReadAll(r.Body)
			received += len(body)
			state = "analyzing"
		case "POST /app/import/start":
			state = "running"
		case "GET /app/import":
			switch state {
			case "analyzing":
				state = "ready"
			case "running":
				state = "completed"
			}
		}
		fmt.Fprintf(w, `{"id":%q,"project":%q,"database":"app","state":%q,"source":{"receivedBytes":%d},`+
			`"preview":{"id":%q,"tables":[{"source":"public.t","collection":"t","rows":2}]},"progress":{"rowsWritten":2},`+
			`"createdAt":"2026-10-23T00:00:00Z","updatedAt":"2026-10-23T00:00:00Z"}`, id, project, state, received, id)
	}))
	defer srv.Close()
	c, err := v1.New(v1.Config{BaseURL: srv.URL, Token: "tb_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"})
	if err != nil {
		t.Fatal(err)
	}
	path := filepath.Join(t.TempDir(), "dump.sql")
	if err := os.WriteFile(path, src, 0o600); err != nil {
		t.Fatal(err)
	}
	var out bytes.Buffer
	if err := importFile(t.Context(), c.Database(project, "app"), "postgres_plain", path, &out); err != nil {
		t.Fatal(err)
	}
	want := []string{"POST ", "POST /app/import", "PUT /app/import/source", "GET /app/import",
		"POST /app/import/start", "GET /app/import"}
	if !slices.Equal(calls, want) || received != len(src) || !strings.Contains(out.String(), "Imported 2 rows into app") {
		t.Fatalf("calls = %q, received = %d, out = %q", calls, received, out.String())
	}
}

func TestCompletionOffersEveryCommand(t *testing.T) {
	for _, shell := range []string{"bash", "zsh", "fish"} {
		var out bytes.Buffer
		if err := run(t.Context(), []string{"completion", shell}, &out); err != nil {
			t.Fatal(err)
		}
		for name := range commands {
			for word := range strings.FieldsSeq(name) {
				if !strings.Contains(out.String(), word) {
					t.Errorf("%s completion lacks %q", shell, word)
				}
			}
		}
	}
	if err := run(t.Context(), []string{"completion", "tcsh"}, io.Discard); !errors.Is(err, errUsage) {
		t.Fatalf("tcsh = %v", err)
	}
}
