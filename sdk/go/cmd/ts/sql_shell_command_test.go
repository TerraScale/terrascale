package main

import (
	"bytes"
	"errors"
	"fmt"
	"net/http"
	"strings"
	"testing"
)

func TestShellCommandUsesSuppliedInputAndPrompt(t *testing.T) {
	t.Parallel()
	s := jsonDatabaseSession(t, func(w http.ResponseWriter, r *http.Request) {
		fmt.Fprint(w, `{"results":[{"columns":[],"rows":[],"rows_affected":1,"last_insert_rowid":null}]}`)
	})
	var out, prompt bytes.Buffer
	input := commandInput{in: strings.NewReader("SELECT 1;\n.run\n"), prompt: &prompt}
	if err := s.dispatch(t.Context(), "shell", functionFlags{}, "", []string{"app"}, input, &jsonOutput{Writer: &out}); err != nil || !strings.Contains(out.String(), `"type":"complete"`) || strings.Contains(out.String(), "History") || prompt.Len() == 0 {
		t.Fatalf("shell stdout=%s diagnostics=%s error=%v", out.String(), prompt.String(), err)
	}
	input.in = strings.NewReader(".mode invalid\n")
	if err := s.dispatch(t.Context(), "shell", functionFlags{}, "", []string{"app"}, input, &out); !errors.Is(err, errUsage) {
		t.Fatalf("invalid shell syntax=%v", err)
	}
}
