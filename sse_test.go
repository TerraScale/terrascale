package sdk

import (
	"context"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"reflect"
	"strings"
	"sync"
	"testing"
	"time"
)

// corpusServerFrames loads the committed sync-v1 server frames used on the
// SSE data wire.
func corpusServerFrames(t testing.TB, names ...string) map[string][]byte {
	t.Helper()
	var corpus pinnedCorpus
	readPinnedJSON(t, "conformance/msgpack/message-vectors.json", &corpus)
	frames := map[string][]byte{}
	for _, vector := range corpus.Vectors {
		for _, name := range names {
			if vector.Name == name {
				wire, err := hex.DecodeString(vector.Hex)
				if err != nil {
					t.Fatalf("vector %q is not hex: %v", vector.Name, err)
				}
				frames[name] = wire
			}
		}
	}
	for _, name := range names {
		if frames[name] == nil {
			t.Fatalf("pinned corpus is missing vector %q", name)
		}
	}
	return frames
}

func writeJSONBody(t testing.TB, w http.ResponseWriter, body map[string]any) {
	t.Helper()
	w.Header().Set("Content-Type", "application/json")
	if err := json.NewEncoder(w).Encode(body); err != nil {
		t.Errorf("encode response: %v", err)
	}
}

func sseDataEvent(id string, frame []byte) string {
	return "id: " + id + "\ndata: " + base64.StdEncoding.EncodeToString(frame) + "\n\n"
}

func TestSubscribeServerSentEventsStreamAndReattach(t *testing.T) {
	frames := corpusServerFrames(t, "snapshot", "delta")
	var mu sync.Mutex
	streamOffsets := []string{}
	writeStream := func(w http.ResponseWriter, events ...string) {
		w.Header().Set("Content-Type", "text/event-stream; charset=utf-8")
		w.Header().Set("Cache-Control", "private, no-store")
		w.WriteHeader(http.StatusOK)
		flusher := w.(http.Flusher)
		_, _ = w.Write([]byte(": ping\n\n"))
		flusher.Flush()
		for _, event := range events {
			_, _ = w.Write([]byte(event))
			flusher.Flush()
		}
	}
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		switch {
		case r.URL.Path == "/api/v1/reactive/views" && r.Method == http.MethodPost:
			if got := r.Header.Get("Authorization"); got != "Bearer sse-secret" {
				t.Errorf("attach authorization = %q", got)
			}
			mu.Lock()
			offset := "5"
			if len(streamOffsets) > 0 {
				// The reattach resumes from the last received offset.
				offset = "9"
			}
			mu.Unlock()
			writeJSONBody(t, w, map[string]any{
				"view": "view-1", "generation": 1, "checkpoint": "cp0",
				"log":    "http://" + r.Host + "/api/v1/reactive/views/view-1/1/log",
				"offset": offset, "capability": "cap-token", "capability_header": "X-TerraScale-Capability",
			})
		case r.URL.Path == "/api/v1/reactive/views/view-1/1/stream":
			if got := r.Header.Get("Accept"); got != "text/event-stream" {
				t.Errorf("stream accept = %q", got)
			}
			if got := r.Header.Get("X-TerraScale-Capability"); got != "cap-token" {
				t.Errorf("stream capability = %q", got)
			}
			if got := r.Header.Get("Authorization"); got != "Bearer sse-secret" {
				t.Errorf("stream authorization = %q", got)
			}
			mu.Lock()
			streamOffsets = append(streamOffsets, r.URL.Query().Get("offset"))
			call := len(streamOffsets)
			mu.Unlock()
			if call == 1 {
				writeStream(w,
					sseDataEvent("7", frames["snapshot"]),
					sseDataEvent("9", frames["delta"]),
					"id: 12\nevent: error\ndata: reactive_unavailable\n\n",
				)
				return
			}
			writeStream(w, sseDataEvent("9", frames["delta"]))
			<-r.Context().Done()
		default:
			http.NotFound(w, r)
		}
	}))
	defer server.Close()

	client, err := NewClient(Config{APIBaseURL: server.URL, HTTPClient: server.Client(), AccessToken: "sse-secret"})
	if err != nil {
		t.Fatal(err)
	}
	sub, err := client.Subscribe(context.Background(), "db", "SELECT * FROM users", nil, SubscriptionOptions{Transport: TransportServerSentEvents})
	if err != nil {
		t.Fatal(err)
	}
	snapshotUpdates, _, _, _, err := decodeReactiveMessagePack(frames["snapshot"], "view-1", 1)
	if err != nil {
		t.Fatal(err)
	}
	deltaUpdates, deltaCP, _, _, err := decodeReactiveMessagePack(frames["delta"], "view-1", 1)
	if err != nil {
		t.Fatal(err)
	}
	var got []Update
	deadline := time.After(5 * time.Second)
collect:
	for {
		select {
		case update, ok := <-sub.Events():
			if !ok {
				break collect
			}
			got = append(got, update)
		case <-deadline:
			t.Fatal("timed out collecting SSE updates")
		}
	}
	want := append(append([]Update(nil), snapshotUpdates...), deltaUpdates...)
	if len(got) != len(want)+1 {
		t.Fatalf("updates = %d, want %d (frames + terminal reset)", len(got), len(want)+1)
	}
	for i := range want {
		if !reflect.DeepEqual(got[i], want[i]) {
			t.Fatalf("update %d = %+v, want %+v", i, got[i], want[i])
		}
	}
	terminal := got[len(got)-1]
	if terminal.Kind != UpdateReset || terminal.Reset == nil || terminal.Reset.Reason != ResetReconnect || !strings.Contains(terminal.Reset.Message, "reactive_unavailable") {
		t.Fatalf("terminal reset = %+v", terminal)
	}
	if cp, offset, _ := sub.progress(); cp != deltaCP || offset != "9" {
		t.Fatalf("progress = (%q, %q), want (%q, \"9\")", cp, offset, deltaCP)
	}

	// An errored stream ends the session; the caller reattaches from its
	// last checkpoint and the new stream resumes at that offset.
	reattached, err := client.Subscribe(context.Background(), "db", "SELECT * FROM users", nil, SubscriptionOptions{
		Transport: TransportServerSentEvents, ResumeFrom: sub.Checkpoint(),
	})
	if err != nil {
		t.Fatal(err)
	}
	select {
	case update, ok := <-reattached.Events():
		if !ok || update.Kind != UpdateInsert {
			t.Fatalf("reattached stream update = %+v ok=%v", update, ok)
		}
	case <-time.After(5 * time.Second):
		t.Fatal("timed out waiting for reattached stream")
	}
	reattached.Close()
	sub.Close()

	mu.Lock()
	defer mu.Unlock()
	if len(streamOffsets) != 2 || streamOffsets[0] != "5" || streamOffsets[1] != "9" {
		t.Fatalf("stream offsets = %v, want [5 9]", streamOffsets)
	}
}

func TestSubscribeServerSentEventsResetOnCleanEOF(t *testing.T) {
	frames := corpusServerFrames(t, "snapshot", "delta")
	var mu sync.Mutex
	streamOffsets := []string{}
	writeStream := func(w http.ResponseWriter, events ...string) {
		w.Header().Set("Content-Type", "text/event-stream; charset=utf-8")
		w.Header().Set("Cache-Control", "private, no-store")
		w.WriteHeader(http.StatusOK)
		flusher := w.(http.Flusher)
		for _, event := range events {
			_, _ = w.Write([]byte(event))
			flusher.Flush()
		}
	}
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		switch {
		case r.URL.Path == "/api/v1/reactive/views" && r.Method == http.MethodPost:
			mu.Lock()
			offset := "5"
			if len(streamOffsets) > 0 {
				// The reattach resumes from the last received offset.
				offset = "9"
			}
			mu.Unlock()
			writeJSONBody(t, w, map[string]any{
				"view": "view-1", "generation": 1, "checkpoint": "cp0",
				"log":    "http://" + r.Host + "/api/v1/reactive/views/view-1/1/log",
				"offset": offset, "capability": "cap-token", "capability_header": "X-TerraScale-Capability",
			})
		case r.URL.Path == "/api/v1/reactive/views/view-1/1/stream":
			mu.Lock()
			streamOffsets = append(streamOffsets, r.URL.Query().Get("offset"))
			call := len(streamOffsets)
			mu.Unlock()
			if call == 1 {
				// The stream drops mid-stream without an error event: the
				// handler returns and the response body hits a clean EOF.
				writeStream(w,
					sseDataEvent("7", frames["snapshot"]),
					sseDataEvent("9", frames["delta"]),
				)
				return
			}
			writeStream(w, sseDataEvent("9", frames["delta"]))
			<-r.Context().Done()
		default:
			http.NotFound(w, r)
		}
	}))
	defer server.Close()

	client, err := NewClient(Config{APIBaseURL: server.URL, HTTPClient: server.Client(), AccessToken: "sse-secret"})
	if err != nil {
		t.Fatal(err)
	}
	sub, err := client.Subscribe(context.Background(), "db", "SELECT * FROM users", nil, SubscriptionOptions{Transport: TransportServerSentEvents})
	if err != nil {
		t.Fatal(err)
	}
	snapshotUpdates, _, _, _, err := decodeReactiveMessagePack(frames["snapshot"], "view-1", 1)
	if err != nil {
		t.Fatal(err)
	}
	deltaUpdates, deltaCP, _, _, err := decodeReactiveMessagePack(frames["delta"], "view-1", 1)
	if err != nil {
		t.Fatal(err)
	}
	var got []Update
	deadline := time.After(5 * time.Second)
collect:
	for {
		select {
		case update, ok := <-sub.Events():
			if !ok {
				break collect
			}
			got = append(got, update)
		case <-deadline:
			t.Fatal("timed out collecting SSE updates")
		}
	}
	want := append(append([]Update(nil), snapshotUpdates...), deltaUpdates...)
	if len(got) != len(want)+1 {
		t.Fatalf("updates = %d, want %d (frames + terminal reset)", len(got), len(want)+1)
	}
	for i := range want {
		if !reflect.DeepEqual(got[i], want[i]) {
			t.Fatalf("update %d = %+v, want %+v", i, got[i], want[i])
		}
	}
	terminal := got[len(got)-1]
	if terminal.Kind != UpdateReset || terminal.Reset == nil || terminal.Reset.Reason != ResetReconnect || !strings.Contains(terminal.Reset.Message, "reactive SSE stream ended") {
		t.Fatalf("terminal reset = %+v", terminal)
	}
	if cp, offset, _ := sub.progress(); cp != deltaCP || offset != "9" {
		t.Fatalf("progress = (%q, %q), want (%q, \"9\")", cp, offset, deltaCP)
	}

	// A dropped stream ends the session with the same reset semantics as an
	// error event; the caller reattaches from its last checkpoint and the new
	// stream resumes at that offset.
	reattached, err := client.Subscribe(context.Background(), "db", "SELECT * FROM users", nil, SubscriptionOptions{
		Transport: TransportServerSentEvents, ResumeFrom: sub.Checkpoint(),
	})
	if err != nil {
		t.Fatal(err)
	}
	select {
	case update, ok := <-reattached.Events():
		if !ok || update.Kind != UpdateInsert {
			t.Fatalf("reattached stream update = %+v ok=%v", update, ok)
		}
	case <-time.After(5 * time.Second):
		t.Fatal("timed out waiting for reattached stream")
	}
	reattached.Close()
	sub.Close()

	mu.Lock()
	defer mu.Unlock()
	if len(streamOffsets) != 2 || streamOffsets[0] != "5" || streamOffsets[1] != "9" {
		t.Fatalf("stream offsets = %v, want [5 9]", streamOffsets)
	}
}

func TestSubscribeServerSentEventsMapsPreStreamFailures(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path == "/api/v1/reactive/views" {
			writeJSONBody(t, w, map[string]any{
				"view": "view-1", "generation": 1, "checkpoint": "cp0",
				"log":    "http://" + r.Host + "/api/v1/reactive/views/view-1/1/log",
				"offset": "0", "capability": "cap-token", "capability_header": "X-TerraScale-Capability",
			})
			return
		}
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(http.StatusNotFound)
		_, _ = w.Write([]byte(`{"error":{"code":"view_generation_expired","message":"generation is gone"}}`))
	}))
	defer server.Close()
	client, err := NewClient(Config{APIBaseURL: server.URL, HTTPClient: server.Client()})
	if err != nil {
		t.Fatal(err)
	}
	sub, err := client.Subscribe(context.Background(), "db", "SELECT * FROM users", nil, SubscriptionOptions{Transport: TransportServerSentEvents})
	if err != nil {
		t.Fatal(err)
	}
	defer sub.Close()
	select {
	case update := <-sub.Events():
		if update.Kind != UpdateReset || update.Reset == nil || update.Reset.Reason != ResetGeneration {
			t.Fatalf("pre-stream reset = %+v", update)
		}
	case <-time.After(5 * time.Second):
		t.Fatal("timed out waiting for pre-stream reset")
	}
}

func TestSubscribeServerSentEventsRejectsInvalidBase64(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path == "/api/v1/reactive/views" {
			writeJSONBody(t, w, map[string]any{
				"view": "view-1", "generation": 1, "checkpoint": "cp0",
				"log":    "http://" + r.Host + "/api/v1/reactive/views/view-1/1/log",
				"offset": "0", "capability": "cap-token", "capability_header": "X-TerraScale-Capability",
			})
			return
		}
		w.Header().Set("Content-Type", "text/event-stream; charset=utf-8")
		_, _ = w.Write([]byte("id: 3\ndata: !!!not-base64!!!\n\n"))
	}))
	defer server.Close()
	client, err := NewClient(Config{APIBaseURL: server.URL, HTTPClient: server.Client()})
	if err != nil {
		t.Fatal(err)
	}
	sub, err := client.Subscribe(context.Background(), "db", "SELECT * FROM users", nil, SubscriptionOptions{Transport: TransportServerSentEvents})
	if err != nil {
		t.Fatal(err)
	}
	defer sub.Close()
	select {
	case update := <-sub.Events():
		if update.Kind != UpdateReset || update.Reset == nil || update.Reset.Reason != ResetReconnect || !strings.Contains(update.Reset.Message, "base64") {
			t.Fatalf("invalid base64 reset = %+v", update)
		}
	case <-time.After(5 * time.Second):
		t.Fatal("timed out waiting for invalid base64 reset")
	}
}
