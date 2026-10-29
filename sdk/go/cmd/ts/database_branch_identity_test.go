package main

import (
	"bytes"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"strings"
	"sync/atomic"
	"testing"

	v1 "terrascale.tech/sdk/v1"
)

func TestExpectedDatabaseBranchUsesOnlyGuardedRoute(t *testing.T) {
	t.Parallel()
	const source = "01ARZ3NDEKTSV4RRFFQ69G5FA9"
	const target = "01ARZ3NDEKTSV4RRFFQ69G5FAA"
	for _, status := range []int{201, 409, 404} {
		t.Run(fmt.Sprint(status), func(t *testing.T) {
			t.Parallel()
			var calls atomic.Int32
			db := exportTestDB(t, func(w http.ResponseWriter, r *http.Request) {
				calls.Add(1)
				if r.Method != "POST" || !strings.HasSuffix(r.URL.Path, "/databases/main/branches/if-id/"+source) || r.URL.RawQuery != "" {
					t.Errorf("unsafe request: %s %s", r.Method, r.URL.Path)
				}
				var body map[string]string
				if err := json.NewDecoder(r.Body).Decode(&body); err != nil || len(body) != 1 || body["name"] != "preview" {
					t.Errorf("unexpected name body: %v", err)
				}
				w.Header().Set("X-Request-ID", "trace-fixture")
				w.WriteHeader(status)
				if status == 201 {
					io.WriteString(w, `{"id":"`+target+`","projectId":"01ARZ3NDEKTSV4RRFFQ69G5FA8","name":"preview","branchedFrom":"`+source+`"}`)
				} else {
					code := "database_identity_changed"
					if status == 404 {
						code = "unknown_route"
					}
					fmt.Fprintf(w, `{"error":{"code":%q,"message":"secret-error-body"}}`, code)
				}
			})
			var out bytes.Buffer
			err := branchExpectedDatabase(t.Context(), db, "preview", source, &jsonOutput{Writer: &out})
			if calls.Load() != 1 || (err == nil) != (status == 201) {
				t.Fatalf("status %d: calls=%d error=%v", status, calls.Load(), err)
			}
			if status == 201 {
				var branch v1.ManagedDatabase
				if err := json.Unmarshal(out.Bytes(), &branch); err != nil || branch.ID != target || branch.Name != "preview" {
					t.Fatalf("unstructured branch output: %q", &out)
				}
			} else {
				if out.Len() != 0 || exitStatus(err) != 1 {
					t.Fatal("refusal emitted success or wrong exit status")
				}
				if emitJSONError(&out, err) != nil || !json.Valid(out.Bytes()) || strings.Contains(out.String(), "secret-error-body") || !strings.Contains(out.String(), `"status":`+fmt.Sprint(status)) {
					t.Fatalf("unsafe failure output: %q", &out)
				}
			}
		})
	}
}

func TestExpectedDatabaseBranchValidationPrecedesAuthentication(t *testing.T) {
	t.Parallel()
	for _, args := range [][]string{
		{"-expected-id", "", "db", "branch", "main", "preview"},
		{"-expected-id", "secret-value", "db", "branch", "main", "preview"},
		{"-expected-id", "01arz3ndektsv4rrffq69g5fav", "db", "branch", "main", "preview"},
		{"-expected-id", "01ARZ3NDEKTSV4RRFFQ69G5FA9", "db", "list"},
		{"-expected-id", "01ARZ3NDEKTSV4RRFFQ69G5FA9", "db", "branch", "secret-value", "secret-value"},
		{"db", "branch", "secret-value", "secret-value"},
	} {
		var out bytes.Buffer
		err := runWithInput(t.Context(), append([]string{"--json"}, args...), &out, nil, io.Discard, false)
		if !errors.Is(err, errUsage) || exitStatus(err) != 2 || !json.Valid(out.Bytes()) || strings.Contains(out.String(), "secret-value") || strings.Contains(err.Error(), "secret-value") {
			t.Fatalf("invalid branch usage: %v %q", err, &out)
		}
	}
}

func TestExpectedDatabaseBranchHelpAndValueFlags(t *testing.T) {
	t.Parallel()
	var out bytes.Buffer
	if err := runWithInput(t.Context(), []string{"--json", "db", "branch", "--help"}, &out, nil, io.Discard, false); err != nil || !strings.Contains(out.String(), "-expected-id") {
		t.Fatalf("branch help: %v %q", err, &out)
	}
	if _, enabled, err := jsonArguments([]string{"-expected-id", "--json", "db", "branch", "main", "preview"}); err != nil || enabled {
		t.Fatal("expected-id value became an output flag")
	}
	for _, shell := range []string{"bash", "zsh", "fish"} {
		out.Reset()
		if err := completion(shell, &out); err != nil || !strings.Contains(out.String(), "expected-id") {
			t.Fatalf("%s completion: %v", shell, err)
		}
	}
}
