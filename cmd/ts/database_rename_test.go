package main

import (
	"bytes"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/http/httptest"
	"slices"
	"sync"
	"testing"

	v1 "terrascale.tech/sdk/v1"
)

func TestDatabaseRenameCommand(t *testing.T) {
	t.Parallel()
	const project, id = "01M5N8CG630T6P449ES15GNZ8N", "01ARZ3NDEKTSV4RRFFQ69G5FAW"
	const base = "/v1/projects/" + project + "/databases/"
	for _, selector := range []string{id, "orders"} {
		t.Run(selector, func(t *testing.T) {
			t.Parallel()
			var mu sync.Mutex
			var calls []string
			server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				body, _ := io.ReadAll(r.Body)
				mu.Lock()
				calls = append(calls, r.Method+" "+r.URL.RequestURI()+" "+string(body))
				mu.Unlock()
				if r.Header.Get("Authorization") != "Bearer header.project.signature" || r.Header.Get("Idempotency-Key") != "" {
					t.Error("unexpected authorization or mutation replay header")
				}
				name := "orders"
				if r.Method == http.MethodPatch {
					name = "orders_new"
					if r.Header.Get("Content-Type") != "application/json" {
						t.Error("PATCH must send JSON")
					}
				}
				w.Header().Set("Content-Type", "application/json")
				fmt.Fprintf(w, `{"id":%q,"projectId":%q,"name":%q}`, id, project, name)
			}))
			defer server.Close()
			client, err := v1.New(v1.Config{BaseURL: server.URL, Token: "header.project.signature"})
			if err != nil {
				t.Fatal(err)
			}
			s := &session{tb: client, project: project}
			var out bytes.Buffer
			if err := s.dispatch(t.Context(), "db rename", functionFlags{}, "", []string{selector, "orders_new"}, &out); err != nil {
				t.Fatal(err)
			}
			want := []string{`PATCH ` + base + id + ` {"name":"orders_new"}`}
			if selector != id {
				want = append([]string{"GET " + base + selector + " "}, want...)
			}
			mu.Lock()
			defer mu.Unlock()
			if !slices.Equal(calls, want) || out.String() != "Renamed "+selector+" to orders_new ("+id+")\n" {
				t.Fatalf("requests = %q, output = %q", calls, out.String())
			}
		})
	}
}

func TestDatabaseRenameCommandFailures(t *testing.T) {
	t.Parallel()
	const project, id = "01M5N8CG630T6P449ES15GNZ8N", "01ARZ3NDEKTSV4RRFFQ69G5FAW"
	for _, tc := range []struct {
		name, selector, newName, resolvedID, code string
		status, wantCalls                         int
	}{
		{"conflict", "orders", "orders_new", id, "name_taken", 409, 2},
		{"invalid server name", id, "con", id, "invalid_request", 400, 1},
		{"wrong region", id, "orders_new", id, "wrong_region", 421, 1},
		{"uncertain write", "orders", "orders_new", id, "write_outcome_unknown", 503, 2},
		{"missing source", "orders", "orders_new", "", "unknown_database", 404, 1},
		{"invalid metadata ID", "orders", "orders_new", "orders", "", 0, 1},
		{"empty name", id, "", id, "", 0, 0},
		{"malformed name", id, "Bad Name", id, "", 0, 0},
		{"ULID as name", id, id, id, "", 0, 0},
		{"invalid selector", "../escape", "orders_new", id, "", 0, 0},
	} {
		t.Run(tc.name, func(t *testing.T) {
			t.Parallel()
			var mu sync.Mutex
			calls := 0
			server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				mu.Lock()
				calls++
				mu.Unlock()
				w.Header().Set("Content-Type", "application/json")
				if r.Method == http.MethodGet && tc.resolvedID != "" {
					fmt.Fprintf(w, `{"id":%q,"projectId":%q,"name":"orders"}`, tc.resolvedID, project)
					return
				}
				if tc.status == 0 {
					t.Error("invalid input reached mutation transport")
					w.WriteHeader(http.StatusInternalServerError)
					return
				}
				w.WriteHeader(tc.status)
				fmt.Fprintf(w, `{"error":{"code":%q,"message":"fixture refusal"}}`, tc.code)
			}))
			defer server.Close()
			client, err := v1.New(v1.Config{BaseURL: server.URL, Token: "header.project.signature"})
			if err != nil {
				t.Fatal(err)
			}
			s := &session{tb: client, project: project}
			var out bytes.Buffer
			err = s.dispatch(t.Context(), "db rename", functionFlags{}, "", []string{tc.selector, tc.newName}, &out)
			var api *v1.APIError
			if err == nil || out.Len() != 0 {
				t.Fatalf("error = %v, output = %q", err, out.String())
			}
			if tc.status != 0 && (!errors.As(err, &api) || api.Status != tc.status || string(api.Code) != tc.code) {
				t.Fatalf("lost API error: %v", err)
			}
			mu.Lock()
			defer mu.Unlock()
			if calls != tc.wantCalls {
				t.Fatalf("requests = %d, want %d", calls, tc.wantCalls)
			}
		})
	}
}
