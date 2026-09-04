package sdk

import (
	"context"
	"encoding/hex"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync"
	"testing"
	"time"
)

func mustHex(value string) []byte {
	body, err := hex.DecodeString(value)
	if err != nil {
		panic(err)
	}
	return body
}

func TestReactiveMessagePackRejectsMapsAndHostileBounds(t *testing.T) {
	for name, wire := range map[string]string{
		"map":              "81a16101",
		"oversized binary": "c60100000000",
		"trailing":         "9702010003000092a173960100009192a17390a0c401637f",
	} {
		t.Run(name, func(t *testing.T) {
			body, err := hex.DecodeString(wire)
			if err != nil {
				t.Fatal(err)
			}
			if _, _, _, _, err = decodeReactiveMessagePack(body, "v", 1); err == nil {
				t.Fatal("hostile MessagePack was accepted")
			}
		})
	}
}

func TestReactiveMessagePackDecodesIndexedSnapshot(t *testing.T) {
	body, err := hex.DecodeString("9702010003000092a173960100009192a17390a0c40163")
	if err != nil {
		t.Fatal(err)
	}
	updates, checkpoint, offset, reset, err := decodeReactiveMessagePack(body, "v", 1)
	if err != nil {
		t.Fatal(err)
	}
	if reset != nil || len(updates) != 1 || updates[0].ID != "s" || checkpoint != "c" || offset != "" {
		t.Fatalf("decoded snapshot = updates=%+v checkpoint=%q offset=%q reset=%v", updates, checkpoint, offset, reset)
	}
}

func TestSubscribeUsesCanonicalOffsetCapabilityAndCopiesSnapshotBody(t *testing.T) {
	var mu sync.Mutex
	var requests []string
	secondRequest := make(chan struct{}, 1)
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		switch {
		case r.URL.Path == "/api/v1/reactive/views" && r.Method == http.MethodPost:
			_ = json.NewEncoder(w).Encode(map[string]any{
				"view": "view-1", "generation": 7, "checkpoint": "opaque-0",
				"log":    "http://" + r.Host + "/attacker-path?offset=0&ignored=must-drop",
				"offset": 0, "capability": "cap-1", "capability_header": "X-TerraScale-View-Capability",
				"snapshot": []map[string]any{{"id": "doc-1", "body": json.RawMessage(`{"a":1}`)}},
			})
		case strings.HasSuffix(r.URL.Path, "/api/v1/reactive/views/view-1/7/log"):
			mu.Lock()
			requests = append(requests, r.URL.RequestURI()+" "+r.Header.Get("X-TerraScale-Capability")+" "+r.Header.Get("X-TerraScale-View-Capability"))
			n := len(requests)
			mu.Unlock()
			if n == 2 {
				secondRequest <- struct{}{}
			}
			if n == 1 {
				w.Header().Set("ETag", `"rev-1"`)
				w.Header().Set("Cache-Control", "private, max-age=0")
				w.Header().Set("Content-Type", "application/msgpack")
				_, _ = w.Write(mustHex("9702010004040497a173070304919401a165019192a178c40169a0c40169"))
				return
			}
			w.Header().Set("Cache-Control", "no-store")
			w.Header().Set("Content-Type", "application/msgpack")
			_, _ = w.Write(mustHex("9702010004040497a17307040490a0c40169"))
		default:
			http.NotFound(w, r)
		}
	}))
	defer server.Close()

	client, err := NewClient(Config{APIBaseURL: server.URL, HTTPClient: server.Client()})
	if err != nil {
		t.Fatal(err)
	}
	ctx, cancel := context.WithCancel(context.Background())
	sub, err := client.Subscribe(ctx, "db", "SELECT * FROM users", nil, SubscriptionOptions{})
	if err != nil {
		t.Fatal(err)
	}
	first := <-sub.Events()
	if first.Kind != UpdateSnapshot || string(first.Body) != `{"a":1}` {
		t.Fatalf("snapshot = %+v", first)
	}
	first.Body[5] = '9'
	second := <-sub.Events()
	if second.Kind != UpdateInsert || second.Checkpoint != "i" || second.ID != "e" {
		t.Fatalf("delta = %+v", second)
	}
	if sub.Checkpoint() != "i" {
		t.Fatalf("checkpoint = %q", sub.Checkpoint())
	}
	select {
	case <-secondRequest:
	case <-time.After(time.Second):
		t.Fatal("poll did not advance to the next offset")
	}
	cancel()
	sub.Close()

	mu.Lock()
	got := append([]string(nil), requests...)
	mu.Unlock()
	if len(got) == 0 || got[0] != "/api/v1/reactive/views/view-1/7/log?offset=0  cap-1" {
		t.Fatalf("first log request = %q", got)
	}
	if len(got) < 2 || got[1] != "/api/v1/reactive/views/view-1/7/log?offset=4  cap-1" {
		t.Fatalf("second log request = %q", got)
	}
}

func TestSubscribeMapsRetentionHTTPReset(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path == "/api/v1/reactive/views" {
			_ = json.NewEncoder(w).Encode(map[string]any{
				"viewId": "view-1", "generation": 2, "checkpoint": "cp0",
				"logUrl": "http://" + r.Host + "/log", "capability": "cap",
			})
			return
		}
		w.Header().Set("Cache-Control", "no-store")
		w.WriteHeader(http.StatusGone)
		_ = json.NewEncoder(w).Encode(map[string]any{"reset": map[string]any{"reason": "retention", "message": "expired"}})
	}))
	defer server.Close()
	client, err := NewClient(Config{APIBaseURL: server.URL, HTTPClient: server.Client()})
	if err != nil {
		t.Fatal(err)
	}
	sub, err := client.Subscribe(context.Background(), "db", "SELECT * FROM users", nil, SubscriptionOptions{})
	if err != nil {
		t.Fatal(err)
	}
	ev := <-sub.Events()
	if ev.Kind != UpdateReset || ev.Reset == nil || ev.Reset.Reason != ResetHistoryExpired || ev.Reset.ViewID != "view-1" {
		t.Fatalf("reset = %+v", ev)
	}
	sub.Close()
}

func TestPollRevalidatesImmutableOffsetButDropsNoStoreETag(t *testing.T) {
	var calls int
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		calls++
		if calls == 1 {
			if got := r.Header.Get("If-None-Match"); got != "" {
				t.Fatalf("first request sent an ETag: %q", got)
			}
			w.Header().Set("ETag", `"offset-7"`)
			w.Header().Set("Content-Type", "application/msgpack")
			_, _ = w.Write(mustHex("9702010006060590"))
			return
		}
		if got := r.Header.Get("If-None-Match"); got != `"offset-7"` {
			t.Errorf("revalidation ETag = %q", got)
		}
		w.WriteHeader(http.StatusNotModified)
	}))
	defer server.Close()
	client, err := NewClient(Config{APIBaseURL: server.URL, HTTPClient: server.Client()})
	if err != nil {
		t.Fatal(err)
	}
	s := &Subscription{c: client, logURL: server.URL + "/log", capability: "cap", offset: "7"}
	_, _, _, etag, _, err := s.pollOnce(context.Background())
	if err != nil || etag != `"offset-7"` {
		t.Fatalf("first poll etag=%q err=%v", etag, err)
	}
	s.setProgress("", "", etag)
	_, _, _, etag, _, err = s.pollOnce(context.Background())
	if err != nil || etag != `"offset-7"` {
		t.Fatalf("304 poll etag=%q err=%v", etag, err)
	}
}

func TestSubscribeRetriesAreBoundedAndCancellationStopsLongPoll(t *testing.T) {
	var logCalls int
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path == "/api/v1/reactive/views" {
			_ = json.NewEncoder(w).Encode(map[string]any{
				"viewId": "view-1", "generation": 2, "checkpoint": "cp0",
				"logUrl": "http://" + r.Host + "/log", "capability": "cap",
			})
			return
		}
		logCalls++
		w.WriteHeader(http.StatusServiceUnavailable)
	}))
	defer server.Close()
	client, err := NewClient(Config{APIBaseURL: server.URL, HTTPClient: server.Client()})
	if err != nil {
		t.Fatal(err)
	}
	sub, err := client.Subscribe(context.Background(), "db", "SELECT * FROM users", nil, SubscriptionOptions{})
	if err != nil {
		t.Fatal(err)
	}
	deadline := time.NewTimer(5 * time.Second)
	defer deadline.Stop()
	select {
	case ev := <-sub.Events():
		if ev.Kind != UpdateReset || ev.Reset == nil || ev.Reset.Reason != ResetReconnect {
			t.Fatalf("bounded retry event = %+v", ev)
		}
	case <-deadline.C:
		t.Fatal("bounded retry did not terminate")
	}
	sub.Close()
	if logCalls > pollRetryLimit+1 {
		t.Fatalf("log calls = %d, want at most %d", logCalls, pollRetryLimit+1)
	}
}
