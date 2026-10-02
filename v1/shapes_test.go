package v1_test

// Fast-suite integration tests for shapes (long-poll, SSE and edge) against
// the contract fixture (fixture_test.go).

import (
	"context"
	"errors"
	"fmt"
	"maps"
	"slices"
	"strings"
	"sync"
	"testing"
	"time"

	v1 "terrascale.tech/sdk/v1"
)

// shapeConsumer applies events to a local replica, like an application would.
type shapeConsumer struct {
	mu     sync.Mutex
	docs   map[string]string
	events []v1.ShapeEvent
	last   v1.ShapeEvent
	done   chan error
}

func consume(ctx context.Context, sub *v1.Subscription) *shapeConsumer {
	c := &shapeConsumer{docs: map[string]string{}, done: make(chan error, 1)}
	go func() {
		for ev, err := range sub.Events(ctx) {
			if err != nil {
				c.done <- err
				return
			}
			c.mu.Lock()
			c.events = append(c.events, ev)
			c.last = ev
			if ev.Type == v1.EventReset {
				clear(c.docs)
			}
			for _, ch := range ev.Changes {
				for _, op := range ch.Ops {
					if op.Op == v1.OpDelete {
						delete(c.docs, op.ID)
					} else {
						c.docs[op.ID] = string(op.Document)
					}
				}
			}
			c.mu.Unlock()
		}
	}()
	return c
}

func (c *shapeConsumer) snapshot() (map[string]string, []v1.ShapeEvent, v1.ShapeEvent) {
	c.mu.Lock()
	defer c.mu.Unlock()
	return maps.Clone(c.docs), slices.Clone(c.events), c.last
}

func countEvents(events []v1.ShapeEvent, keep func(v1.ShapeEvent) bool) int {
	n := 0
	for _, ev := range events {
		if keep(ev) {
			n++
		}
	}
	return n
}

func TestShapeSubscriptions(t *testing.T) {
	t.Parallel()
	modes := []struct {
		name string
		opts []v1.SubscribeOption
	}{
		{"long-poll", nil},
		{"sse", []v1.SubscribeOption{v1.WithSSE()}},
		{"edge", []v1.SubscribeOption{v1.ViaEdge()}},
	}
	for _, mode := range modes {
		t.Run(mode.name, func(t *testing.T) {
			t.Parallel()
			f := newFixture(t)
			f.SetSnapshotPageSize(2)
			if mode.name == "edge" {
				f.SetShapeTokenTTL(400 * time.Millisecond)
			}
			ctx, cancel := context.WithTimeout(t.Context(), 20*time.Second)
			defer cancel()
			db := newClient(t, f, f.dataToken).Database("main")
			notes := db.Collection("notifications")
			put := func(key string, doc map[string]any) *v1.WriteResult {
				t.Helper()
				res, err := notes.Put(ctx, v1.DocumentIDFor("notifications", key), doc)
				if err != nil {
					t.Fatal(err)
				}
				return res
			}
			for i := range 5 {
				put(fmt.Sprint("a", i), map[string]any{"user_id": "u1", "title": fmt.Sprint("hi ", i), "read": false})
			}
			head := put("b0", map[string]any{"user_id": "u2", "title": "other", "read": false})
			req := v1.ShapeRequest{Collection: "notifications", Filter: v1.Eq("/user_id", "u1"), Project: []string{"/title", "/read"}}
			sub, err := db.Subscribe(ctx, req, mode.opts...)
			if err != nil {
				t.Fatal(err)
			}
			c := consume(ctx, sub)
			wait := func(offset v1.Uint64) map[string]string {
				t.Helper()
				if err := sub.WaitFor(ctx, offset); err != nil {
					t.Fatalf("WaitFor(%d): %v", offset, err)
				}
				docs, _, _ := c.snapshot()
				return docs
			}

			// Snapshot: 5 matching documents over 3 pages, projected.
			docs := wait(head.Offset)
			_, events, _ := c.snapshot()
			if len(docs) != 5 || docs[v1.DocumentIDFor("notifications", "a0")] != `{"read":false,"title":"hi 0"}` {
				t.Fatalf("snapshot replica: %v", docs)
			}
			if n := countEvents(events, func(e v1.ShapeEvent) bool { return e.Snapshot && e.Type == v1.EventChanges }); n != 3 {
				t.Fatalf("snapshot pages = %d, want 3: %+v", n, events)
			}

			// Read-your-writes on the live log: insert, leave the shape,
			// delete, and an unrelated commit that only advances offsets.
			docs = wait(put("a9", map[string]any{"user_id": "u1", "title": "new", "read": false}).Offset)
			if docs[v1.DocumentIDFor("notifications", "a9")] != `{"read":false,"title":"new"}` {
				t.Fatalf("live insert: %v", docs)
			}
			docs = wait(put("a1", map[string]any{"user_id": "u2", "title": "moved"}).Offset)
			if _, ok := docs[v1.DocumentIDFor("notifications", "a1")]; ok {
				t.Fatal("document leaving the shape must be deleted")
			}
			del, err := notes.Delete(ctx, v1.DocumentIDFor("notifications", "a2"))
			if err != nil {
				t.Fatal(err)
			}
			if docs = wait(del.Offset); len(docs) != 4 {
				t.Fatalf("after delete: %v", docs)
			}
			wait(put("b1", map[string]any{"user_id": "u2"}).Offset)

			if mode.name == "edge" {
				// Outlive several token lifetimes; the client refreshes
				// through the gatekeeper before expiry, so the edge never
				// rejects a token.
				time.Sleep(1600 * time.Millisecond)
				wait(put("a8", map[string]any{"user_id": "u1", "title": "after refresh"}).Offset)
				if calls := f.count("POST", "/v1/databases/main/shapes"); calls < 3 {
					t.Fatalf("gatekeeper calls = %d, want token refreshes", calls)
				}
				if f.count("EDGE", "401") != 0 || f.count("EDGE", "GET") == 0 {
					t.Fatalf("edge: %d requests, %d rejected", f.count("EDGE", "GET"), f.count("EDGE", "401"))
				}
			}
			if sse := f.count("GET", "/v1/shapes/sse"); (mode.name == "sse") != (sse > 0) {
				t.Fatalf("%s mode made %d SSE requests", mode.name, sse)
			}
			if mode.name != "sse" {
				log := strings.Join(f.ShapeReadLog(), "\n")
				for _, want := range []string{"offset=-1", "offset=snap.", "&live=true&cursor="} {
					if !strings.Contains(log, want) {
						t.Fatalf("shape requests lack %q:\n%s", want, log)
					}
				}
			}

			// must_refetch: the replica is reset and rebuilt from a snapshot.
			// The reads that follow hit transient failures, which the
			// subscription retries without surfacing them.
			before, _, _ := c.snapshot()
			f.inject(fxFault{method: "GET", pathPrefix: "/v1/shapes/", status: 429, code: "rate_limited", retryAfter: "0"})
			f.inject(fxFault{method: "GET", pathPrefix: "/v1/shapes/", status: 503, code: "storage_unavailable"})
			f.rotateHandle(sub.Info().ShapeID)
			docs = wait(put("a7", map[string]any{"user_id": "u1", "title": "after reset"}).Offset)
			if left := f.faultsLeft(); left != 0 {
				t.Fatalf("%d injected faults were not hit", left)
			}
			_, events, last := c.snapshot()
			if countEvents(events, func(e v1.ShapeEvent) bool { return e.Type == v1.EventReset }) != 1 || len(docs) != len(before)+1 {
				t.Fatalf("reset: %d docs before, %v after", len(before), docs)
			}

			// Close ends the stream and WaitFor.
			sub.Close()
			if err := <-c.done; !errors.Is(err, v1.ErrSubscriptionClosed) {
				t.Fatalf("consumer ended with %v", err)
			}
			if err := sub.WaitFor(ctx, sub.Position()+100); !errors.Is(err, v1.ErrSubscriptionClosed) {
				t.Fatalf("WaitFor after Close: %v", err)
			}

			// Resume from the saved position: only newer changes arrive.
			resumed, err := db.Subscribe(ctx, req, append(mode.opts, v1.ResumeAt(last.Handle, last.NextOffset))...)
			if err != nil {
				t.Fatal(err)
			}
			defer resumed.Close()
			res := put("a6", map[string]any{"user_id": "u1", "title": "resumed"})
			ev, err := resumed.Next(ctx)
			if err != nil || ev.Type != v1.EventChanges || ev.Snapshot || len(ev.Changes) != 1 || ev.Changes[0].Offset != res.Offset {
				t.Fatalf("resumed event: %+v %v", ev, err)
			}
		})
	}
}
