package main

import (
	"bytes"
	"encoding/base64"
	"encoding/json"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func TestJSONFunctionFamilies(t *testing.T) {
	t.Parallel()
	for _, command := range []string{"functions deploy", "functions list", "functions invoke", "functions logs", "functions delete"} {
		t.Run(command, func(t *testing.T) {
			t.Parallel()
			s := jsonDatabaseSession(t, func(w http.ResponseWriter, r *http.Request) {
				w.Header().Set("Content-Type", "application/json")
				switch command {
				case "functions deploy":
					io.WriteString(w, `{"name":"hello"}`)
				case "functions list", "functions logs":
					io.WriteString(w, `[]`)
				case "functions invoke":
					io.WriteString(w, `{"n":9007199254740993}`)
				default:
					w.WriteHeader(http.StatusNoContent)
				}
			})
			args := []string{"hello"}
			if command == "functions deploy" {
				path := filepath.Join(t.TempDir(), "hello.js")
				if err := os.WriteFile(path, []byte(`function handler() {return 1}`), 0600); err != nil {
					t.Fatal(err)
				}
				args = []string{path}
			}
			var buf bytes.Buffer
			err := functions(t.Context(), s.tb.Database(s.project, "app"), command, functionFlags{}, args, &jsonOutput{Writer: &buf})
			if err != nil || !json.Valid(buf.Bytes()) {
				t.Fatalf("function JSON: %v", err)
			}
			if command == "functions invoke" && !strings.Contains(buf.String(), `"body":{"n":9007199254740993}`) {
				t.Fatal("handler payload lost")
			}
			if command == "functions logs" || command == "functions list" {
				if buf.String() != "[]\n" {
					t.Fatal("empty list must be []")
				}
			}
		})
	}
	var buf bytes.Buffer
	if err := emitFunctionResponse(&buf, []byte{0xff, 0, 1}); err != nil {
		t.Fatal(err)
	}
	var body struct{ BodyBase64 string }
	if err := json.Unmarshal(buf.Bytes(), &body); err != nil || body.BodyBase64 != base64.StdEncoding.EncodeToString([]byte{0xff, 0, 1}) {
		t.Fatal("binary handler payload lost")
	}
}

func TestJSONImportReturnsFinalJob(t *testing.T) {
	t.Parallel()
	s := jsonDatabaseSession(t, func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		if strings.HasSuffix(r.URL.Path, "/import") {
			io.WriteString(w, `{"id":"`+jsonAuthID+`","project":"`+jsonAuthID+`","database":"app","state":"completed","progress":{"rowsWritten":2}}`)
		} else {
			io.WriteString(w, `{"id":"`+jsonAuthID+`","projectId":"`+jsonAuthID+`","name":"app"}`)
		}
	})
	path := filepath.Join(t.TempDir(), "source.ndjson")
	if err := os.WriteFile(path, []byte("{}\n"), 0600); err != nil {
		t.Fatal(err)
	}
	var buf bytes.Buffer
	err := importFile(t.Context(), s.tb.Database(s.project, "app"), "ndjson", path, "people", &jsonOutput{Writer: &buf})
	if err != nil || !json.Valid(buf.Bytes()) || !strings.Contains(buf.String(), `"state":"completed"`) {
		t.Fatalf("import JSON: %v", err)
	}
	var progress bytes.Buffer
	out := &jsonOutput{Writer: &buf, progress: &progress}
	io.WriteString(progressOutput(out), "progress-marker")
	if progress.String() != "progress-marker" || strings.Contains(buf.String(), "progress-marker") {
		t.Fatal("progress mixed with payload")
	}
}
