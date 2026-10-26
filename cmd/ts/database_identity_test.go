package main

import (
	"bytes"
	"errors"
	"fmt"
	"io"
	"net/http"
	"strings"
	"sync/atomic"
	"testing"
)

func TestExpectedDatabaseDeletionNeverMutatesByName(t *testing.T) {
	t.Parallel()
	const id = "01ARZ3NDEKTSV4RRFFQ69G5FA9"
	for _, status := range []int{204, 409, 404} {
		t.Run(fmt.Sprint(status), func(t *testing.T) {
			t.Parallel()
			var calls atomic.Int32
			db := exportTestDB(t, func(w http.ResponseWriter, r *http.Request) {
				calls.Add(1)
				if r.Method != "DELETE" || !strings.HasSuffix(r.URL.Path, "/databases/main/if-id/"+id) {
					t.Errorf("unsafe request: %s %s", r.Method, r.URL.Path)
				}
				w.WriteHeader(status)
				if status != 204 {
					io.WriteString(w, `{"error":{"code":"database_identity_mismatch","message":"not deleted"}}`)
				}
			})
			var out bytes.Buffer
			err := deleteExpectedDatabase(t.Context(), db, id, &jsonOutput{Writer: &out})
			if (err == nil) != (status == 204) || calls.Load() != 1 {
				t.Fatalf("status %d: calls=%d error=%v", status, calls.Load(), err)
			}
			if status == 204 && out.String() != "{\"name\":\"main\",\"deleted\":true}\n" || status != 204 && out.Len() != 0 {
				t.Fatalf("unexpected success output: %q", out.String())
			}
		})
	}
}

func TestExpectedDatabaseIDValidationPrecedesAuthentication(t *testing.T) {
	t.Parallel()
	for _, args := range [][]string{
		{"-expected-id", "", "db", "delete", "main"},
		{"-expected-id", "secret-value", "db", "delete", "main"},
		{"-expected-id", "01ARZ3NDEKTSV4RRFFQ69G5FA9", "db", "list"},
	} {
		var out bytes.Buffer
		err := runWithInput(t.Context(), append([]string{"--json"}, args...), &out, nil, io.Discard, false)
		if !errors.Is(err, errUsage) || strings.Contains(out.String(), "secret-value") {
			t.Fatalf("invalid expected ID = %v, %q", err, out.String())
		}
	}
	if _, enabled, err := jsonArguments([]string{"-expected-id", "--json", "db", "delete", "main"}); enabled || err != nil {
		t.Fatal("expected-id value interpreted as an output flag")
	}
}
