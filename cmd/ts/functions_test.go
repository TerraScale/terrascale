package main

import (
	"bytes"
	"errors"
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

func TestFunctionsCommands(t *testing.T) {
	const project = "01M5N8CG630T6P449ES15GNZ8N"
	const fn = `"createdAt":"2026-10-24T12:00:00Z","updatedAt":"2026-10-24T12:00:00Z"`
	var mu sync.Mutex
	var calls []string
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		body, _ := io.ReadAll(r.Body)
		call := r.Method + " " + strings.TrimPrefix(r.URL.Path, "/v1/projects/"+project+"/databases/app/functions")
		if r.URL.RawQuery != "" {
			call += "?" + r.URL.RawQuery
		}
		mu.Lock()
		calls = append(calls, strings.TrimSpace(call+" "+string(body)))
		mu.Unlock()
		w.Header().Set("Content-Type", "application/json")
		switch call {
		case "PUT /hello":
			io.WriteString(w, `{"name":"hello","source":"x","trigger":null,"cron":null,`+fn+`}`)
		case "GET ":
			io.WriteString(w, `[{"name":"hello","source":null,"trigger":{"collection":"items","events":["delete"]},"cron":"0 * * * *",`+fn+`}]`)
		case "POST /hello/invoke":
			w.WriteHeader(http.StatusBadRequest)
			io.WriteString(w, `{"reason":"bad"}`)
		case "GET /hello/logs?limit=2":
			io.WriteString(w, `[{"id":"01M5N9000000000000000000AA","at":"2026-10-24T12:00:00Z","trigger":"http","status":"ok","durationMs":3,"lines":["hi"],"error":null}]`)
		case "DELETE /hello":
			w.WriteHeader(http.StatusNoContent)
		}
	}))
	defer srv.Close()
	c, err := v1.New(v1.Config{BaseURL: srv.URL, Token: "tb_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", MaxRetries: -1})
	if err != nil {
		t.Fatal(err)
	}
	db := c.Database(project, "app")
	dir := filepath.Join(t.TempDir(), "hello")
	if err := os.Mkdir(dir, 0o700); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(dir, "index.js"), []byte("x"), 0o600); err != nil {
		t.Fatal(err)
	}
	var out bytes.Buffer
	for _, args := range [][]string{
		{"functions deploy", dir, "-trigger-collection", "items", "-events", "upsert,delete", "-cron", "* * * * *"},
		{"functions list"},
		{"functions invoke", "hello", "-data", `{"n":1}`},
		{"functions logs", "hello", "-limit", "2"},
		{"functions delete", "hello"},
	} {
		var f functionFlags
		operands, err := f.parse(args[0], args[1:])
		if err != nil {
			t.Fatal(err)
		}
		err = functions(t.Context(), db, args[0], f, operands, &out)
		if args[0] == "functions invoke" {
			if api := (*v1.APIError)(nil); !errors.As(err, &api) || api.Status != http.StatusBadRequest {
				t.Fatal("invoke hid the handler's 400")
			}
		} else if err != nil {
			t.Fatalf("%s: %v", args[0], err)
		}
	}
	want := []string{`PUT /hello {"source":"x","trigger":{"collection":"items","events":["upsert","delete"]},"cron":"* * * * *"}`,
		"GET", `POST /hello/invoke {"n":1}`, "GET /hello/logs?limit=2", "DELETE /hello"}
	if !slices.Equal(calls, want) {
		t.Fatalf("calls = %q", calls)
	}
	for _, line := range []string{"Deployed function hello", "hello  items:delete  0 * * * *  2026-10-24T12:00:00Z", `{"reason":"bad"}`,
		`"lines":["hi"]`, "Deleted function hello"} {
		if !strings.Contains(out.String(), line) {
			t.Errorf("output lacks %q:\n%s", line, out.String())
		}
	}
}
