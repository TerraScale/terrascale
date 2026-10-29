package main

import (
	"bytes"
	"errors"
	"io"
	"net/http"
	"net/http/httptest"
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
		{"backup", "restore", "app", "id"}} {
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
