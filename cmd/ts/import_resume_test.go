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

	v1 "terrascale.tech/sdk/v1"
)

func TestImportFileReattachment(t *testing.T) {
	t.Parallel()
	for _, tc := range []struct {
		name, state, resumed, polled, wantErr                                     string
		missingSource, noPreview, noProgress, blocking, rejectResume, outputError bool
		wantCalls                                                                 []string
	}{
		{name: "completed rerun", state: "completed", missingSource: true},
		{name: "queued", state: "queued", missingSource: true, wantCalls: []string{"GET "}},
		{name: "running", state: "running", missingSource: true, wantCalls: []string{"GET "}},
		{name: "validating", state: "validating", missingSource: true, wantCalls: []string{"GET "}},
		{name: "cancelled", state: "cancelled", resumed: "queued", wantCalls: []string{"POST /resume", "GET "}},
		{name: "failed", state: "failed", resumed: "running", wantCalls: []string{"POST /resume", "GET "}},
		{name: "expired source", state: "cancelled", missingSource: true, resumed: "queued", wantCalls: []string{"PUT /source", "POST /resume", "GET "}},
		{name: "unconfirmed resume", state: "cancelled", resumed: "analyzing", polled: "ready", wantCalls: []string{"POST /resume", "GET ", "POST /start", "GET "}},
		{name: "analyzing", state: "analyzing", polled: "ready", wantCalls: []string{"GET ", "POST /start", "GET "}},
		{name: "ready", state: "ready", wantCalls: []string{"POST /start", "GET "}},
		{name: "resume rejected", state: "failed", rejectResume: true, wantErr: "invalid_state", wantCalls: []string{"POST /resume"}},
		{name: "rejected", state: "rejected", missingSource: true, wantErr: "import rejected: bad_source: invalid source"},
		{name: "unknown state", state: "unknown", missingSource: true, wantErr: "import unknown"},
		{name: "missing state", missingSource: true, wantErr: "import "},
		{name: "missing preview", state: "ready", noPreview: true, wantErr: "import ready"},
		{name: "preview output failed", state: "ready", outputError: true, wantErr: "output closed"},
		{name: "blocking preview", state: "ready", blocking: true, wantErr: "1 blocking issues"},
		{name: "missing progress", state: "completed", noProgress: true, wantErr: "import completed"},
		{name: "failure while attached", state: "running", polled: "failed", wantErr: "import failed", wantCalls: []string{"GET "}},
	} {
		t.Run(tc.name, func(t *testing.T) {
			t.Parallel()
			const project, id = "01M5N8CG630T6P449ES15GNZ8N", "01M5N9000000000000000000AA"
			const source = "{\"name\":\"Ana\"}\n"
			job := v1.ImportJob{ID: id, Project: project, Database: "app", State: tc.state,
				Source:  v1.ImportSource{ReceivedBytes: int64(len(source))},
				Preview: &v1.ImportPreview{ID: id}, Progress: &v1.ImportProgress{RowsWritten: 1}}
			if tc.missingSource {
				job.Source.ReceivedBytes = 0
			}
			if tc.noPreview {
				job.Preview = nil
			}
			if tc.noProgress {
				job.Progress = nil
			}
			if tc.blocking {
				job.Preview.Blocking = 1
			}
			if tc.state == "rejected" {
				job.Error = &v1.ImportError{Code: "bad_source", Message: "invalid source"}
			}
			var mu sync.Mutex
			var calls []string
			srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				mu.Lock()
				defer mu.Unlock()
				call := r.Method + " " + strings.TrimPrefix(r.URL.Path, "/v1/projects/"+project+"/databases")
				calls = append(calls, call)
				w.Header().Set("Content-Type", "application/json")
				switch call {
				case "POST ":
					w.WriteHeader(http.StatusConflict)
					io.WriteString(w, `{"error":{"code":"name_taken","message":"database exists"}}`)
					return
				case "POST /app/import":
					var req v1.CreateImportRequest
					if err := json.NewDecoder(r.Body).Decode(&req); err != nil || req.Format != "ndjson" ||
						req.SourceBytes != int64(len(source)) || req.SourceSHA256 != fmt.Sprintf("%x", sha256.Sum256([]byte(source))) ||
						req.Options == nil || req.Options.Collection != "people" {
						t.Errorf("declaration = %+v, error = %v", req, err)
					}
				case "PUT /app/import/source":
					body, _ := io.ReadAll(r.Body)
					if string(body) != source || r.URL.Query().Get("offset") != "0" {
						t.Errorf("upload = %q at %s", body, r.URL.RawQuery)
					}
					job.Source.ReceivedBytes = int64(len(source))
				case "POST /app/import/resume":
					if tc.rejectResume {
						w.WriteHeader(http.StatusConflict)
						io.WriteString(w, `{"error":{"code":"invalid_state","message":"cannot resume this job"}}`)
						return
					}
					job.State = tc.resumed
				case "POST /app/import/start":
					var req struct {
						PreviewID string `json:"previewId"`
					}
					if err := json.NewDecoder(r.Body).Decode(&req); err != nil || req.PreviewID != id {
						t.Errorf("confirmation = %+v, error = %v", req, err)
					}
					job.State = "running"
				case "GET /app/import":
					job.State = "completed"
					if tc.polled != "" && !slices.Contains(calls, "POST /app/import/start") {
						job.State = tc.polled
					}
				default:
					t.Errorf("unexpected request %s", call)
				}
				json.NewEncoder(w).Encode(job)
			}))
			t.Cleanup(srv.Close)
			c, err := v1.New(v1.Config{BaseURL: srv.URL, Token: "tb_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"})
			if err != nil {
				t.Fatal(err)
			}
			path := filepath.Join(t.TempDir(), "people.ndjson")
			if err := os.WriteFile(path, []byte(source), 0o600); err != nil {
				t.Fatal(err)
			}
			var out bytes.Buffer
			var output io.Writer = &out
			if tc.outputError {
				output = importClosedWriter{}
			}
			err = importFile(t.Context(), c.Database(project, "app"), "ndjson", path, "people", output)
			if tc.wantErr != "" {
				if err == nil || !strings.Contains(err.Error(), tc.wantErr) {
					t.Fatalf("error = %v, want %q", err, tc.wantErr)
				}
			} else if err != nil || !strings.Contains(out.String(), "Imported 1 rows into app") {
				t.Fatalf("error = %v, output = %q", err, out.String())
			}
			want := []string{"POST ", "POST /app/import"}
			for _, call := range tc.wantCalls {
				want = append(want, strings.Replace(call, " ", " /app/import", 1))
			}
			mu.Lock()
			defer mu.Unlock()
			if !slices.Equal(calls, want) {
				t.Fatalf("requests = %q, want %q", calls, want)
			}
		})
	}
}

type importClosedWriter struct{}

func (importClosedWriter) Write([]byte) (int, error) { return 0, errors.New("output closed") }
