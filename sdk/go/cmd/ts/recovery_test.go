package main

import (
	"bytes"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"sync/atomic"
	"testing"
	"time"

	v1 "terrascale.tech/sdk/v1"
)

func recoverySession(t *testing.T, handler http.HandlerFunc) *session {
	t.Helper()
	srv := httptest.NewServer(handler)
	t.Cleanup(srv.Close)
	c, err := v1.New(v1.Config{BaseURL: srv.URL, Token: "tb_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"})
	if err != nil {
		t.Fatal(err)
	}
	return &session{tb: c, project: "01M5N8CG630T6P449ES15GNZ8N"}
}

func recoveryFile(t *testing.T, value any) string {
	t.Helper()
	data, err := json.Marshal(value)
	if err != nil {
		t.Fatal(err)
	}
	path := filepath.Join(t.TempDir(), "records.json")
	if err := os.WriteFile(path, data, 0600); err != nil {
		t.Fatal(err)
	}
	return path
}

func TestRecoveryPreviewThenExplicitSubset(t *testing.T) {
	t.Parallel()
	var calls atomic.Int32
	tombstone, live := "tombstone-etag", "live-etag"
	preview := v1.RecoveryPreview{Approval: "fake-approval", ExpiresAt: time.Now().Add(10 * time.Minute), Records: []v1.RecoveryRecord{
		{Collection: "orders", ID: "01ARZ3NDEKTSV4RRFFQ69G5FAW", Historical: json.RawMessage(`{"total":9007199254740993}`), Current: json.RawMessage(`null`), CurrentVersion: &tombstone},
		{Collection: "orders", ID: "01ARZ3NDEKTSV4RRFFQ69G5FAX", Historical: json.RawMessage(`{"total":2}`), Current: json.RawMessage(`{"total":3}`), CurrentVersion: &live},
	}}
	s := recoverySession(t, func(w http.ResponseWriter, r *http.Request) {
		calls.Add(1)
		if r.Method != http.MethodPost || r.Header.Get("Authorization") == "" {
			t.Error("expected authenticated POST")
		}
		body, _ := io.ReadAll(r.Body)
		switch r.URL.Path {
		case "/v1/projects/01M5N8CG630T6P449ES15GNZ8N/databases/orders/point-in-time/recovery-preview":
			if string(body) != `{"pointInTime":"2026-10-25T10:00:00Z","selections":[{"collection":"orders","ids":["01ARZ3NDEKTSV4RRFFQ69G5FAW","01ARZ3NDEKTSV4RRFFQ69G5FAX"]}]}` {
				t.Errorf("unexpected preview request: %s", body)
			}
			json.NewEncoder(w).Encode(preview)
		case "/v1/projects/01M5N8CG630T6P449ES15GNZ8N/databases/orders/point-in-time/recover":
			if string(body) != `{"approval":"fake-approval","records":[{"collection":"orders","id":"01ARZ3NDEKTSV4RRFFQ69G5FAW"}]}` {
				t.Errorf("apply sent content, versions or unapproved records: %s", body)
			}
			io.WriteString(w, `{"offset":"9007199254740993","durability":"local_durable","results":[{"id":"01ARZ3NDEKTSV4RRFFQ69G5FAW","version":"new-etag"}]}`)
		default:
			t.Errorf("unexpected request path: %s", r.URL.Path)
			w.WriteHeader(http.StatusNotFound)
		}
	})
	selection := recoveryFile(t, []v1.RecoverySelection{{Collection: "orders", IDs: []string{"01ARZ3NDEKTSV4RRFFQ69G5FAW", "01ARZ3NDEKTSV4RRFFQ69G5FAX"}}})
	var out bytes.Buffer
	if err := s.run(t.Context(), "recovery preview", []string{"orders", "2026-10-25T10:00:00Z", selection}, &jsonOutput{Writer: &out}); err != nil {
		t.Fatal(err)
	}
	if calls.Load() != 1 || !strings.Contains(out.String(), "9007199254740993") || !strings.Contains(out.String(), "tombstone-etag") {
		t.Fatal("preview applied records or lost exact historical/current state")
	}
	// Editing historical JSON never supplies apply data: only the server token
	// and explicitly approved identities leave the CLI.
	preview.Records[0].Historical = json.RawMessage(`{"edited":true}`)
	approved := recoveryFile(t, []v1.RecoveryApproval{{Collection: "orders", ID: "01ARZ3NDEKTSV4RRFFQ69G5FAW"}})
	out.Reset()
	if err := s.run(t.Context(), "recovery apply", []string{"orders", recoveryFile(t, preview), approved}, &jsonOutput{Writer: &out}); err != nil {
		t.Fatal(err)
	}
	if calls.Load() != 2 || !strings.Contains(out.String(), `"offset":"9007199254740993"`) {
		t.Fatal("wrong apply result or unexpected requests")
	}
	// A failed JSON write must suppress a second error envelope, which could
	// corrupt the partial preview or append metadata after an approval token.
	failed := &jsonOutput{Writer: brokenPrompt{}}
	if err := s.run(t.Context(), "recovery preview", []string{"orders", "2026-10-25T10:00:00Z", selection}, failed); !errors.Is(err, io.ErrClosedPipe) || !failed.failed || calls.Load() != 3 {
		t.Fatal("JSON writer failure was not preserved without retry")
	}
}

func TestRecoveryRefusesInvalidApprovalAndDoesNotRetryConflicts(t *testing.T) {
	t.Parallel()
	var calls atomic.Int32
	s := recoverySession(t, func(w http.ResponseWriter, r *http.Request) {
		calls.Add(1)
		w.WriteHeader(http.StatusConflict)
		io.WriteString(w, `{"error":{"code":"recovery_conflict","message":"preview again"}}`)
	})
	preview := v1.RecoveryPreview{Approval: "fake-approval", ExpiresAt: time.Now().Add(10 * time.Minute), Records: []v1.RecoveryRecord{{Collection: "orders", ID: "01ARZ3NDEKTSV4RRFFQ69G5FAW"}}}
	file := recoveryFile(t, preview)
	for _, input := range []any{
		[]v1.RecoveryApproval{},
		[]v1.RecoveryApproval{{Collection: "orders", ID: "01ARZ3NDEKTSV4RRFFQ69G5FAY"}},
		[]v1.RecoveryApproval{{Collection: "orders", ID: "01ARZ3NDEKTSV4RRFFQ69G5FAW"}, {Collection: "orders", ID: "01ARZ3NDEKTSV4RRFFQ69G5FAW"}},
		[]map[string]any{{"collection": "orders", "id": "01ARZ3NDEKTSV4RRFFQ69G5FAW", "historical": map[string]any{"edited": true}}},
	} {
		if err := s.run(t.Context(), "recovery apply", []string{"orders", file, recoveryFile(t, input)}, io.Discard); err == nil {
			t.Fatal("invalid approval was accepted")
		}
	}
	approved := recoveryFile(t, []v1.RecoveryApproval{{Collection: "orders", ID: "01ARZ3NDEKTSV4RRFFQ69G5FAW"}})
	preview.ExpiresAt = time.Now().Add(-time.Minute)
	if err := s.run(t.Context(), "recovery apply", []string{"orders", recoveryFile(t, preview), approved}, io.Discard); err == nil || calls.Load() != 0 {
		t.Fatal("expired or invalid approval reached HTTP")
	}
	if err := s.run(t.Context(), "recovery apply", []string{"orders", file, approved}, io.Discard); err == nil || calls.Load() != 1 {
		t.Fatal("stale conflict was hidden or replayed")
	}
}
