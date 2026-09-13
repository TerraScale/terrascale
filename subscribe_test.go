package sdk

import (
	"context"
	"encoding/hex"
	"encoding/json"
	"errors"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/coder/websocket"
)

func TestSubscribeWebSocketLoopback(t *testing.T) {
	protocols := make(chan string, 1)
	mux := http.NewServeMux()
	mux.HandleFunc("/api/v1/reactive/views", func(w http.ResponseWriter, r *http.Request) {
		if got := r.Header.Get("Authorization"); got != "Bearer websocket-secret" {
			t.Errorf("attach authorization = %q", got)
		}
		if r.Method != http.MethodPost {
			t.Errorf("attach method = %s", r.Method)
		}
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write([]byte(`{"view":"view-1","generation":1,"checkpoint":"Y3A=","log":"/api/v1/reactive/views/view-1/1/log","capability":"cap-token","snapshot":[{"id":"doc-1","body":{"ok":true}}]}`))
	})
	mux.HandleFunc("/api/v1/reactive/views/view-1/1/websocket", func(w http.ResponseWriter, r *http.Request) {
		if got := r.Header.Get("Authorization"); got != "Bearer websocket-secret" {
			t.Errorf("websocket authorization = %q", got)
		}
		protocols <- r.Header.Get("Sec-WebSocket-Protocol")
		conn, err := websocket.Accept(w, r, &websocket.AcceptOptions{Subprotocols: []string{"terrascale.sync.v1"}})
		if err != nil {
			return
		}
		defer conn.Close(websocket.StatusNormalClosure, "done")
		ctx := r.Context()
		typ, frame, err := conn.Read(ctx)
		if err != nil || typ != websocket.MessageBinary || len(frame) < 7 || frame[0] != 0x97 {
			return
		}
		heartbeat := []byte{0x97, 0xcf, 0, 0, 0, 0, 0xcf, 0, 0, 0, 0, 0xcf, 0, 0, 0, 0, 0xcf, 6, 0xcf, 0, 0, 0, 0xcf, 0, 0, 0, 0, 0x90}
		_ = conn.Write(ctx, websocket.MessageBinary, heartbeat)
	})
	server := httptest.NewServer(mux)
	defer server.Close()
	client, err := NewClient(Config{APIBaseURL: server.URL, HTTPClient: server.Client(), AccessToken: "  bEaReR websocket-secret  "})
	if err != nil {
		t.Fatal(err)
	}
	sub, err := client.Subscribe(context.Background(), "db", "SELECT * FROM users", nil, SubscriptionOptions{Transport: TransportWebSocket})
	if err != nil {
		t.Fatal(err)
	}
	defer sub.Close()
	if got := <-protocols; got != "terrascale.sync.v1,terrascale.cap.cap-token" {
		t.Fatalf("protocols = %q", got)
	}
	select {
	case update := <-sub.Events():
		if update.Kind != UpdateSnapshot || update.ID != "doc-1" {
			t.Fatalf("update = %#v", update)
		}
	case <-time.After(time.Second):
		t.Fatal("timed out waiting for snapshot")
	}
}

func TestBearerAuthorizationCanonicalizesCredentials(t *testing.T) {
	for _, tc := range []struct {
		name  string
		input string
		want  string
		err   string
	}{
		{name: "bare", input: "secret", want: "Bearer secret"},
		{name: "prefixed case and whitespace", input: "  bEaReR   secret  ", want: "Bearer secret"},
		{name: "empty", input: "  "},
		{name: "empty prefixed", input: "Bearer   ", err: "sdk: bearer token cannot be empty"},
		{name: "control character", input: "secret\nvalue", err: "sdk: access token contains invalid control characters"},
		{name: "control character hidden by trimming", input: " \n ", err: "sdk: access token contains invalid control characters"},
		{name: "internal whitespace", input: "secret value", err: "sdk: bearer token contains whitespace"},
		{name: "internal whitespace after prefix", input: "Bearer secret value", err: "sdk: bearer token contains whitespace"},
		{name: "tab between prefix and credential", input: "Bearer\tsecret", err: "sdk: access token contains invalid control characters"},
	} {
		t.Run(tc.name, func(t *testing.T) {
			header := make(http.Header)
			err := setBearerAuthorization(header, tc.input)
			if tc.err != "" {
				if err == nil || err.Error() != tc.err {
					t.Fatalf("error = %v, want %q", err, tc.err)
				}
				return
			}
			if err != nil {
				t.Fatal(err)
			}
			if got := header.Get("Authorization"); got != tc.want {
				t.Fatalf("authorization = %q, want %q", got, tc.want)
			}
		})
	}
}

func TestReactiveWebSocketServerControlTags(t *testing.T) {
	snapshot := append(mpArray(6), mpUintValue(1)...)
	snapshot = append(snapshot, mpUintValue(2)...)
	snapshot = append(snapshot, mpUintValue(3)...)
	snapshot = append(snapshot, mpArray(0)...)
	snapshot = append(snapshot, mpString("digest")...)
	snapshot = append(snapshot, mpBinaryValue([]byte("cp"))...)
	auth := append(mpArray(4), mpString("sub")...)
	auth = append(auth, mpUintValue(2)...)
	auth = append(auth, mpArray(0)...)
	auth = append(auth, snapshot...)
	cases := []struct {
		name   string
		frame  []byte
		reason ResetReason
	}{
		{name: "heartbeat", frame: reactiveTestEnvelope(6, mpArray(0)), reason: ""},
		{name: "authorization changed", frame: reactiveTestEnvelope(8, auth), reason: ResetAuthChanged},
		{name: "route change", frame: reactiveTestEnvelope(9, append(mpArray(1), mpString("GRU")...)), reason: ResetRegionMoved},
		{name: "backpressure", frame: reactiveTestEnvelope(11, append(mpArray(1), mpUintValue(1)...)), reason: ResetBackpressure},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			_, _, _, reset, err := decodeReactiveMessagePack(tc.frame, "view", 1)
			if err != nil {
				t.Fatal(err)
			}
			if tc.reason == "" {
				if reset != nil {
					t.Fatalf("heartbeat reset = %#v", reset)
				}
				return
			}
			if reset == nil || reset.Reason != tc.reason {
				t.Fatalf("reset = %#v, want %s", reset, tc.reason)
			}
		})
	}
}

func reactiveTestEnvelope(tag uint64, body []byte) []byte {
	frame := mpArray(7)
	for _, value := range []uint64{1, 0, 0, tag, 0, 0} {
		frame = append(frame, mpUintValue(value)...)
	}
	return append(frame, body...)
}

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
		"trailing":         "9701000003000092a173960100009192a17390a0c401637f",
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

func TestReactiveMessagePackRejectsClientFramesBatchesAndDuplicateFields(t *testing.T) {
	if _, _, _, _, err := decodeReactiveMessagePack(mustHex("9701000001010094a173a171c4020102c0"), "v", 1); err == nil {
		t.Fatal("client-to-server subscribe frame was accepted as a response")
	}
	single := mustHex("9701000006060590")
	batch := append([]byte{0x92}, append(append([]byte(nil), single...), single...)...)
	if _, _, _, _, err := decodeReactiveMessagePack(batch, "v", 1); err == nil {
		t.Fatal("batched response envelopes were accepted")
	}
	duplicate := mustHex("9701000003000092a173960100009192a17392a173c40161a173c40162a0c40163")
	if _, _, _, _, err := decodeReactiveMessagePack(duplicate, "v", 1); err == nil {
		t.Fatal("duplicate projected fields were accepted")
	}
}

func TestReactiveMessagePackDecodesIndexedSnapshot(t *testing.T) {
	body, err := hex.DecodeString("9701000003000092a173960100009192a17390a0c40163")
	if err != nil {
		t.Fatal(err)
	}
	updates, checkpoint, offset, reset, err := decodeReactiveMessagePack(body, "v", 1)
	if err != nil {
		t.Fatal(err)
	}
	if reset != nil || len(updates) != 1 || updates[0].ID != "s" || updates[0].Position != 0 || updates[0].ViewGeneration != 1 || updates[0].Revision != 0 || checkpoint != "c" || offset != "" {
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
			var request map[string]any
			if err := json.NewDecoder(r.Body).Decode(&request); err != nil {
				t.Errorf("attach request: %v", err)
			}
			if request["databaseId"] != "db" || request["statement"] != "SELECT * FROM users" || request["parameters"] != "e30=" {
				t.Errorf("attach request = %+v", request)
			}
			if _, ok := request["transport"]; ok {
				t.Error("attach request invented undeclared transport member")
			}
			_ = json.NewEncoder(w).Encode(map[string]any{
				"view": "view-1", "generation": 7, "checkpoint": "opaque-0",
				"log":    "http://" + r.Host + "/attacker-path?offset=0&ignored=must-drop",
				"offset": 0, "capability": "cap-1", "capability_header": "X-TerraScale-Capability",
				"snapshot": []map[string]any{{"id": "doc-1", "body": json.RawMessage(`{"a":1}`)}},
			})
		case strings.HasSuffix(r.URL.Path, "/api/v1/reactive/views/view-1/7/log"):
			mu.Lock()
			requests = append(requests, r.URL.RequestURI()+" "+r.Header.Get("X-TerraScale-Capability")+" "+r.Header.Get("X-TerraScale-View-Capability")+" "+r.Header.Get("Authorization"))
			n := len(requests)
			mu.Unlock()
			if n == 2 {
				secondRequest <- struct{}{}
			}
			if n == 1 {
				w.Header().Set("ETag", `"rev-1"`)
				w.Header().Set("Cache-Control", "private, max-age=0")
				w.Header().Set("Content-Type", "application/msgpack")
				_, _ = w.Write(mustHex("9701000004040497a173070304919401a165019192a178c40169a0c40169"))
				return
			}
			w.Header().Set("Cache-Control", "no-store")
			w.Header().Set("Content-Type", "application/msgpack")
			_, _ = w.Write(mustHex("9701000004040497a17307040490a0c40169"))
		default:
			http.NotFound(w, r)
		}
	}))
	defer server.Close()

	client, err := NewClient(Config{APIBaseURL: server.URL, HTTPClient: server.Client(), AccessToken: "  bEaReR poll-secret  "})
	if err != nil {
		t.Fatal(err)
	}
	ctx, cancel := context.WithCancel(context.Background())
	sub, err := client.Subscribe(ctx, "db", "SELECT * FROM users", nil, SubscriptionOptions{})
	if err != nil {
		t.Fatal(err)
	}
	first := <-sub.Events()
	if first.Kind != UpdateSnapshot || first.Position != 0 || string(first.Body) != `{"a":1}` {
		t.Fatalf("snapshot = %+v", first)
	}
	first.Body[5] = '9'
	second := <-sub.Events()
	if second.Kind != UpdateInsert || second.Checkpoint != "i" || second.ID != "e" || second.Position != 1 || second.ViewGeneration != 7 || second.FromRevision != 3 || second.ToRevision != 4 || second.Digest != "" {
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
	if len(got) == 0 || got[0] != "/api/v1/reactive/views/view-1/7/log?offset=0 cap-1  Bearer poll-secret" {
		t.Fatalf("first log request = %q", got)
	}
	if len(got) < 2 || got[1] != "/api/v1/reactive/views/view-1/7/log?offset=4 cap-1  Bearer poll-secret" {
		t.Fatalf("second log request = %q", got)
	}
}

func TestCapabilityHeaderRejectsLegacyViewAlias(t *testing.T) {
	if got, err := normalizeCapabilityHeader("X-TerraScale-Capability"); err != nil || got != "X-TerraScale-Capability" {
		t.Fatalf("canonical header = %q, err=%v", got, err)
	}
	if _, err := normalizeCapabilityHeader("X-TerraScale-View-Capability"); err == nil {
		t.Fatal("legacy view capability header was accepted")
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

func TestPollPreservesStructuredAPIErrorDetails(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("X-Request-ID", "reactive-request-1")
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(http.StatusBadRequest)
		_, _ = w.Write([]byte(`{"error":{"code":"invalid_checkpoint","message":"checkpoint rejected","fix":"start a fresh snapshot"}}`))
	}))
	defer server.Close()

	client, err := NewClient(Config{APIBaseURL: server.URL, HTTPClient: server.Client()})
	if err != nil {
		t.Fatal(err)
	}
	s := &Subscription{c: client, logURL: server.URL + "/log", viewID: "view-1", generation: 3}
	_, _, _, _, _, err = s.pollOnce(t.Context())
	var apiErr *APIError
	if !errors.As(err, &apiErr) {
		t.Fatalf("poll error = %T %v, want APIError", err, err)
	}
	if apiErr.Code != "invalid_checkpoint" || apiErr.Message != "checkpoint rejected" || apiErr.Fix != "start a fresh snapshot" || apiErr.RequestID != "reactive-request-1" {
		t.Fatalf("poll API error = %+v", apiErr)
	}
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
			_, _ = w.Write(mustHex("9701000006060590"))
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
	select {
	case <-sub.done:
	case <-time.After(time.Second):
		t.Fatal("terminal reset did not release the subscription without explicit Close")
	}
	sub.Close()
	if logCalls > pollRetryLimit+1 {
		t.Fatalf("log calls = %d, want at most %d", logCalls, pollRetryLimit+1)
	}
}
