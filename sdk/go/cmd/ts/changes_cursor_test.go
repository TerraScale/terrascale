package main

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"strings"
	"testing"
	"time"

	v1 "terrascale.tech/sdk/v1"
)

type changeCancelWriter struct {
	bytes.Buffer
	cancel context.CancelFunc
	left   int
}

func (w *changeCancelWriter) Write(p []byte) (int, error) {
	n, err := w.Buffer.Write(p)
	w.left--
	if w.left == 0 {
		w.cancel()
	}
	return n, err
}

func TestChangesCursorParserBoundariesOffline(t *testing.T) {
	t.Parallel()
	args := []string{"db", "changes", "orders"}
	for i := 0; i < 65; i++ {
		args = append(args, fmt.Sprintf("col_%d", i))
	}
	if err := runWithInput(t.Context(), args, io.Discard, nil, io.Discard, false); !errors.Is(err, errUsage) {
		t.Fatalf("65-filter parser = %v", err)
	}
	if err := runWithInput(t.Context(), []string{"-cursor", "resume", "db", "list"}, io.Discard, nil, io.Discard, false); !errors.Is(err, errUsage) {
		t.Fatalf("unscoped cursor = %v", err)
	}
	if _, structured, err := jsonArguments([]string{"-cursor", "--json", "db", "changes", "orders"}); err != nil || structured {
		t.Fatal("cursor value became an output flag")
	}
}

func TestChangesCursorAndCommitGroupOutput(t *testing.T) {
	t.Parallel()
	for _, structured := range []bool{false, true} {
		t.Run(fmt.Sprint(structured), func(t *testing.T) {
			t.Parallel()
			ctx, cancel := context.WithTimeout(t.Context(), 5*time.Second)
			defer cancel()
			c := managementTerraBase(t, func(w http.ResponseWriter, r *http.Request) {
				if r.URL.Query().Get("cursor") != "resume" || len(strings.Split(r.URL.Query().Get("collections"), ",")) != 64 {
					t.Error("cursor or collection filters not forwarded")
				}
				w.Header().Set("Content-Type", "text/event-stream")
				io.WriteString(w, "event: cursor\nid: initial\ndata: {\"cursor\":\"initial\"}\n\n")
				for _, id := range []string{"", "checkpoint"} {
					fmt.Fprintf(w, "event: change\nid: %s\ndata: {\"collection\":\"people\",\"id\":\"%s\",\"op\":\"upsert\",\"version\":\"1\",\"origin\":\"0000000000000001\",\"document\":{\"n\":9007199254740993}}\n\n", id, managementProjectID)
				}
				w.(http.Flusher).Flush()
				<-r.Context().Done()
			})
			filters := make([]string, 64)
			for i := range filters {
				filters[i] = fmt.Sprintf("col_%d", i)
			}
			buffer := &changeCancelWriter{cancel: cancel, left: 3}
			var out io.Writer = buffer
			if structured {
				out = &jsonOutput{Writer: buffer}
			}
			err := tailChangesFrom(ctx, c.Database(managementProjectID, "orders"), "resume", filters, out)
			if structured && !errors.Is(err, context.Canceled) || !structured && err != nil {
				t.Fatalf("stream cancellation = %v", err)
			}
			lines := strings.Split(strings.TrimSpace(buffer.String()), "\n")
			if len(lines) != 3 || !strings.Contains(lines[0], `"cursor":"initial"`) || strings.Contains(lines[1], `"cursor"`) || !strings.Contains(lines[2], `"cursor":"checkpoint"`) || !strings.Contains(lines[2], "9007199254740993") {
				t.Fatalf("incorrect checkpoint stream %q", buffer.String())
			}
			for _, line := range lines {
				if !json.Valid([]byte(line)) {
					t.Fatal("invalid stream JSON")
				}
			}
		})
	}
}

func TestChangesCursorExpiredIsNotRestarted(t *testing.T) {
	t.Parallel()
	c := managementTerraBase(t, func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Query().Get("cursor") != "expired" {
			t.Error("resume cursor changed")
		}
		w.WriteHeader(http.StatusGone)
		io.WriteString(w, `{"error":{"code":"cursor_expired","message":"expired"}}`)
	})
	if err := tailChangesFrom(t.Context(), c.Database(managementProjectID, "orders"), "expired", nil, io.Discard); !errors.Is(err, v1.ErrCursorExpired) {
		t.Fatalf("expired cursor error = %v", err)
	}
}

func TestChangesRejectsTooManyFiltersAndWriterFailure(t *testing.T) {
	t.Parallel()
	c := managementTerraBase(t, func(w http.ResponseWriter, _ *http.Request) {
		w.Header().Set("Content-Type", "text/event-stream")
		io.WriteString(w, "event: cursor\nid: first\ndata: {\"cursor\":\"first\"}\n\n")
	})
	db := c.Database(managementProjectID, "orders")
	if err := tailChangesFrom(t.Context(), db, "now", make([]string, 65), io.Discard); !errors.Is(err, errUsage) {
		t.Fatalf("filter count error = %v", err)
	}
	if err := tailChangesFrom(t.Context(), db, "now", nil, managementBrokenWriter{}); !errors.Is(err, io.ErrClosedPipe) {
		t.Fatalf("stream writer error = %v", err)
	}
}
