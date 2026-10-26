package main

import (
	"bytes"
	"crypto/sha256"
	"encoding/json"
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
	"time"

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
	for _, args := range [][]string{{"auth"}, {"db"}, {"-nope"}, {"version", "extra"}, {"--version", "extra"},
		{"db", "list", "extra"}, {"db", "create"}, {"doc", "put", "db", "col"}, {"doc", "put", "db", "col", "id", "{}", "x"},
		{"keys", "create"}, {"whoami", "x"}, {"frob"}, {"-project", "p"}, {"db", "regions", "app"},
		{"backup", "restore", "app", "id"}, {"projects", "create"}, {"usage", "a", "b"}, {"completion"},
		{"index", "create", "app", "col"}, {"index", "create", "app", "col", "a", "b", "c", "d", "e"},
		{"schema", "set", "app", "col"}, {"ttl", "set", "app", "col"}, {"import", "app", "sqlite"},
		{"import", "status"}, {"backup", "restore-at", "app", "2026-10-23T00:00:00Z"}, {"db", "changes"},
		{"functions", "list"}, {"functions", "invoke", "app"}, {"functions", "logs", "app", "fn", "-limit", "101"},
		{"functions", "delete", "app", "fn", "-data", "{}"}, {"functions", "deploy", "app", "f.js", "-events", "delete"}} {
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
	for _, args := range [][]string{nil, {"help"}, {"-h"}, {"--help"}} {
		var out bytes.Buffer
		if err := run(t.Context(), args, &out); err != nil {
			t.Fatalf("run(%q) = %v", args, err)
		}
		if out.String() != usage {
			t.Fatalf("run(%q) printed %q", args, out.String())
		}
	}
	for _, tc := range []struct {
		args []string
		want string
	}{{[]string{"db", "--help"}, "ts db create <database>"}, {[]string{"-project", "p", "doc", "put", "-help"}, "ts doc put"}} {
		var out bytes.Buffer
		if err := run(t.Context(), tc.args, &out); err != nil || !strings.Contains(out.String(), tc.want) || strings.Contains(out.String(), "migrate up") {
			t.Fatalf("scoped help(%q) = %q, %v", tc.args, out.String(), err)
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
	if err := importFile(t.Context(), c.Database(project, "app"), "postgres_plain", path, "", &out); err != nil {
		t.Fatal(err)
	}
	want := []string{"POST ", "POST /app/import", "PUT /app/import/source", "GET /app/import",
		"POST /app/import/start", "GET /app/import"}
	if !slices.Equal(calls, want) || received != len(src) || !strings.Contains(out.String(), "Imported 2 rows into app") {
		t.Fatalf("calls = %q, received = %d, out = %q", calls, received, out.String())
	}
}

func TestImportNDJSONSendsCollection(t *testing.T) {
	t.Parallel()
	const project, id = "01M5N8CG630T6P449ES15GNZ8N", "01M5N9000000000000000000AA"
	src := []byte("{\"name\":\"Ana\"}\n")
	var mu sync.Mutex
	state, received, declarations := "awaiting_source", 0, 0
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		mu.Lock()
		defer mu.Unlock()
		w.Header().Set("Content-Type", "application/json")
		switch r.Method + " " + strings.TrimPrefix(r.URL.Path, "/v1/projects/"+project+"/databases") {
		case "POST ":
			w.WriteHeader(http.StatusCreated)
			fmt.Fprintf(w, `{"id":%q,"projectId":%q,"name":"app"}`, id, project)
			return
		case "POST /app/import":
			declarations++
			var req struct {
				Format       string            `json:"format"`
				SourceBytes  int64             `json:"sourceBytes"`
				SourceSHA256 string            `json:"sourceSha256"`
				Options      map[string]string `json:"options"`
			}
			if err := json.NewDecoder(r.Body).Decode(&req); err != nil || req.Format != "ndjson" ||
				req.SourceBytes != int64(len(src)) || req.SourceSHA256 != fmt.Sprintf("%x", sha256.Sum256(src)) ||
				req.Options["collection"] != "people" {
				t.Errorf("import request = %+v, decode error = %v", req, err)
			}
		case "PUT /app/import/source":
			body, _ := io.ReadAll(r.Body)
			if !bytes.Equal(body, src) {
				t.Errorf("source = %q, want %q", body, src)
			}
			received, state = len(body), "ready"
		case "POST /app/import/start":
			state = "completed"
		case "GET /app/import":
		default:
			t.Errorf("unexpected request: %s %s", r.Method, r.URL.Path)
		}
		fmt.Fprintf(w, `{"id":%q,"project":%q,"database":"app","state":%q,"source":{"receivedBytes":%d},`+
			`"preview":{"id":%q,"tables":[{"source":"ndjson","collection":"people","rows":1}]},"progress":{"rowsWritten":1}}`,
			id, project, state, received, id)
	}))
	t.Cleanup(srv.Close)
	c, err := v1.New(v1.Config{BaseURL: srv.URL, Token: "tb_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"})
	if err != nil {
		t.Fatal(err)
	}
	path := filepath.Join(t.TempDir(), "people.ndjson")
	if err := os.WriteFile(path, src, 0o600); err != nil {
		t.Fatal(err)
	}
	s := &session{tb: c, project: project}
	var out bytes.Buffer
	if err := s.dispatch(t.Context(), "import", functionFlags{}, "people", []string{"app", "ndjson", path}, &out); err != nil {
		t.Fatal(err)
	}
	if declarations != 1 || !strings.Contains(out.String(), "Imported 1 rows into app") {
		t.Fatalf("declarations = %d, output = %q", declarations, out.String())
	}
}

func TestCollectionFlagRejectsMisuseBeforeAuthentication(t *testing.T) {
	t.Parallel()
	for _, args := range [][]string{
		{"-collection", "people", "db", "list"},
		{"-collection", "people", "auth", "login"},
		{"-collection", "people", "completion", "bash"},
		{"-collection", "people", "import", "status", "app"},
		{"-collection", "people", "import", "cancel", "app"},
		{"-collection", "people", "import", "app", "sqlite", "dump.db"},
		{"-collection", "people", "import", "app", "postgres_plain", "dump.sql"},
		{"-collection", "people", "import", "app", "json", "dump.json"},
		{"-collection=", "db", "list"},
		{"-collection=", "import", "app", "ndjson", "people.ndjson"},
		{"-collection", " ", "import", "app", "ndjson", "people.ndjson"},
		{"import", "app", "ndjson", "people.ndjson", "-collection", "people"},
	} {
		if err := run(t.Context(), args, io.Discard); !errors.Is(err, errUsage) {
			t.Errorf("run(%q) = %v, want usage", args, err)
		}
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

func TestNewKeyRequestFlags(t *testing.T) {
	req, err := newKeyRequest([]string{"ci", "app"}, "", "")
	if err != nil || req.ExpiresAt != nil || req.AllowedCIDRs != nil || req.Database != "app" || req.Name != "ci" {
		t.Fatalf("default: %+v, %v", req, err)
	}
	req, err = newKeyRequest([]string{"ci"}, "never", " 203.0.113.0/24, 198.51.100.7 ,")
	if err != nil || req.ExpiresAt != nil || !slices.Equal(req.AllowedCIDRs, []string{"203.0.113.0/24", "198.51.100.7"}) {
		t.Fatalf("never and cidrs: %+v, %v", req, err)
	}
	for expires, days := range map[string]int{"30d": 30, "90d": 90, "365d": 365} {
		req, err = newKeyRequest([]string{"ci"}, expires, "")
		if err != nil || req.ExpiresAt == nil {
			t.Fatalf("%s: %+v, %v", expires, req, err)
		}
		if got := time.Until(*req.ExpiresAt).Round(time.Hour); got != time.Duration(days)*24*time.Hour {
			t.Errorf("%s expires in %v", expires, got)
		}
	}
	for _, bad := range [][2]string{{"7d", ""}, {"1y", ""}, {"", ","}, {"", " "}} {
		if _, err := newKeyRequest([]string{"ci"}, bad[0], bad[1]); !errors.Is(err, errUsage) {
			t.Errorf("expires %q cidrs %q: %v, want usage", bad[0], bad[1], err)
		}
	}
}
