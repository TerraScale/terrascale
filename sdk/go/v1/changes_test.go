package v1_test

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"

	v1 "terrascale.tech/sdk/v1"
)

func changesDB(t *testing.T, handler http.HandlerFunc) *v1.Database {
	t.Helper()
	server := httptest.NewServer(handler)
	t.Cleanup(server.Close)
	c, err := v1.New(v1.Config{BaseURL: server.URL, Token: testToken, MaxResponseBytes: 1024})
	if err != nil {
		t.Fatal(err)
	}
	return c.Database(testProject, "main")
}

func TestChangesReconnectAndCursor(t *testing.T) {
	calls := 0
	db := changesDB(t, func(w http.ResponseWriter, r *http.Request) {
		calls++
		if r.URL.Path != "/v1/projects/"+testProject+"/databases/main/changes" || r.URL.Query().Get("cursor") != "now" || r.URL.Query().Get("collections") != "users" || r.Header.Get("Authorization") != "Bearer "+testToken || r.Header.Get("Accept") != "text/event-stream" {
			t.Errorf("request: %s %v", r.URL, r.Header)
		}
		w.Header().Set("Content-Type", "text/event-stream")
		if calls == 1 {
			if r.Header.Get("Last-Event-ID") != "" {
				t.Error("initial resume header")
			}
			fmt.Fprint(w, "event: cursor\nid: AQ\ndata: {\"cursor\":\"AQ\"}\n\n")
		} else {
			if r.Header.Get("Last-Event-ID") != "AQ" {
				t.Error("missing resume header")
			}
			fmt.Fprint(w, "event: change\nid: next\ndata: {\"collection\":\"users\",\"id\":\"01ARZ3NDEKTSV4RRFFQ69G5FD0\",\"op\":\"upsert\",\"version\":\"9007199254740993\",\"origin\":\"0000000000000001\",\"document\":{\"id\":\"application-id\",\"n\":9007199254740993}}\n\n")
		}
	})
	got := 0
	for event, err := range db.Changes(t.Context(), v1.ChangesOptions{Cursor: "now", Collections: []string{"users"}}) {
		if err != nil {
			t.Fatal(err)
		}
		got++
		if got == 1 && (event.Type != "cursor" || event.Cursor != "AQ") {
			t.Fatalf("cursor=%+v", event)
		}
		if got == 2 {
			if event.Version != "9007199254740993" || string(event.Document) != "{\"id\":\"application-id\",\"n\":9007199254740993}" {
				t.Fatalf("change=%+v", event)
			}
			break
		}
	}
	if calls != 2 || got != 2 {
		t.Fatalf("calls=%d events=%d", calls, got)
	}
}

func TestChangesRejectsHTTPAndMalformedEvents(t *testing.T) {
	for _, tc := range []struct {
		name, body string
		status     int
	}{
		{"expired", `{"error":{"code":"cursor_expired","message":"expired"}}`, 410},
		{"unauthorized", `{"error":{"code":"unauthorized","message":"denied"}}`, 401},
		{"forbidden", `{"error":{"code":"forbidden","message":"denied"}}`, 403},
		{"json", "event: change\nid: AQ\ndata: {\n\n", 200},
		{"mismatch", "event: cursor\nid: AQ\ndata: {\"cursor\":\"other\"}\n\n", 200},
		{"truncated", "event: cursor\nid: AQ\ndata: {\"cursor\":\"AQ\"}\n", 200},
		{"bounded", "event: cursor\nid: AQ\ndata: " + strings.Repeat("x", 2048) + "\n\n", 200},
	} {
		t.Run(tc.name, func(t *testing.T) {
			calls := 0
			db := changesDB(t, func(w http.ResponseWriter, r *http.Request) {
				calls++
				w.Header().Set("Content-Type", "text/event-stream")
				w.WriteHeader(tc.status)
				fmt.Fprint(w, tc.body)
			})
			n := 0
			for _, err := range db.Changes(t.Context(), v1.ChangesOptions{Cursor: "saved", Collections: []string{"users"}}) {
				n++
				if err == nil {
					t.Fatal("expected error")
				}
				if tc.status != 200 {
					var api *v1.APIError
					if !errors.As(err, &api) || api.Status != tc.status {
						t.Fatalf("error=%v", err)
					}
				}
			}
			if n != 1 || calls != 1 {
				t.Fatalf("events=%d calls=%d", n, calls)
			}
		})
	}
}

func TestChangesPreservesIssuerRoutingHeaders(t *testing.T) {
	const replay = "instance=issuer-1;timeout=2s;fallback=force_self"
	const body = `{"error":{"code":"cursor_elsewhere","message":"the cursor resumes on its issuing machine"}}`
	calls := 0
	db := changesDB(t, func(w http.ResponseWriter, r *http.Request) {
		calls++
		if r.URL.Query().Get("cursor") != "saved" {
			t.Error("missing resume cursor")
		}
		w.Header().Set("Content-Type", "application/json")
		w.Header().Set("Fly-Replay", replay)
		w.Header().Set("X-Request-ID", "issuer-routing")
		w.WriteHeader(http.StatusMisdirectedRequest)
		fmt.Fprint(w, body)
	})
	failures := 0
	for _, err := range db.Changes(t.Context(), v1.ChangesOptions{Cursor: "saved"}) {
		failures++
		var api *v1.APIError
		if !errors.As(err, &api) || api.Status != http.StatusMisdirectedRequest || api.Code != "cursor_elsewhere" || string(api.Body) != body || api.RequestID != "issuer-routing" || api.Header.Get("Fly-Replay") != replay || api.Header.Get("X-Request-ID") != api.RequestID {
			t.Fatalf("issuer routing error = %#v", err)
		}
	}
	if calls != 1 || failures != 1 {
		t.Fatalf("calls=%d errors=%d; issuer routing must remain caller-controlled", calls, failures)
	}
}

func TestChangesCancellation(t *testing.T) {
	ctx, cancel := context.WithCancel(t.Context())
	defer cancel()
	db := changesDB(t, func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "text/event-stream")
		fmt.Fprint(w, ": heartbeat\n\n")
		w.(http.Flusher).Flush()
		cancel()
		<-r.Context().Done()
	})
	for _, err := range db.Changes(ctx, v1.ChangesOptions{}) {
		if !errors.Is(err, context.Canceled) {
			t.Fatalf("error=%v", err)
		}
	}
}

func TestPaginationRepeatedCursorAndEmptyPage(t *testing.T) {
	calls := 0
	db := changesDB(t, func(w http.ResponseWriter, r *http.Request) {
		calls++
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `{"items":[],"next_cursor":"same"}`)
	})
	pages, errs := 0, 0
	for page, err := range db.Collection("users").Pages(t.Context(), v1.Query{}) {
		if err != nil {
			errs++
			continue
		}
		if page == nil {
			t.Fatal("nil page")
		}
		pages++
	}
	if calls != 2 || pages != 2 || errs != 1 {
		t.Fatalf("calls=%d pages=%d errors=%d", calls, pages, errs)
	}
}

func TestPaginationCancelledBeforeRequest(t *testing.T) {
	calls := 0
	db := changesDB(t, func(w http.ResponseWriter, r *http.Request) { calls++; t.Error("unexpected request") })
	ctx, cancel := context.WithCancel(t.Context())
	cancel()
	errs := 0
	for _, err := range db.Collection("users").Items(ctx, v1.Query{}) {
		errs++
		if !errors.Is(err, context.Canceled) {
			t.Fatal(err)
		}
	}
	if calls != 0 || errs != 1 {
		t.Fatalf("calls=%d errors=%d", calls, errs)
	}
}

func TestPaginationRejectsMissingItems(t *testing.T) {
	for _, body := range []string{`{}`, `{"items":null}`, `{"items":{}}`, `null`} {
		t.Run(body, func(t *testing.T) {
			db := changesDB(t, func(w http.ResponseWriter, r *http.Request) { fmt.Fprint(w, body) })
			if _, err := db.Collection("users").Query(t.Context(), v1.Query{}); err == nil {
				t.Fatal("accepted missing items")
			}
		})
	}
}

func TestPaginationIteratorRestarts(t *testing.T) {
	db := changesDB(t, func(w http.ResponseWriter, r *http.Request) {
		var q struct {
			Cursor string `json:"cursor"`
		}
		if err := json.NewDecoder(r.Body).Decode(&q); err != nil {
			t.Error(err)
		}
		if q.Cursor == "" {
			fmt.Fprint(w, `{"items":[],"next_cursor":"last"}`)
		} else {
			fmt.Fprint(w, `{"items":[],"next_cursor":null}`)
		}
	})
	pages := db.Collection("users").Pages(t.Context(), v1.Query{})
	for range 2 {
		n := 0
		for _, err := range pages {
			if err != nil {
				t.Fatal(err)
			}
			n++
		}
		if n != 2 {
			t.Fatalf("pages=%d", n)
		}
	}
}

func TestItemsCancellationBetweenDocuments(t *testing.T) {
	ctx, cancel := context.WithCancel(t.Context())
	defer cancel()
	db := changesDB(t, func(w http.ResponseWriter, r *http.Request) {
		fmt.Fprint(w, `{"items":[{"id":"01ARZ3NDEKTSV4RRFFQ69G5FD0","version":"1","state":"value","document":{}},{"id":"01ARZ3NDEKTSV4RRFFQ69G5FD1","version":"2","state":"value","document":{}}],"next_cursor":null}`)
	})
	docs, errs := 0, 0
	for _, err := range db.Collection("users").Items(ctx, v1.Query{}) {
		if err != nil {
			errs++
			if !errors.Is(err, context.Canceled) {
				t.Fatal(err)
			}
		} else {
			docs++
			cancel()
		}
	}
	if docs != 1 || errs != 1 {
		t.Fatalf("documents=%d errors=%d", docs, errs)
	}
}

func TestChangesEOFWithoutCursor(t *testing.T) {
	calls := 0
	db := changesDB(t, func(w http.ResponseWriter, r *http.Request) {
		calls++
		w.Header().Set("Content-Type", "text/event-stream")
	})
	n := 0
	for _, err := range db.Changes(t.Context(), v1.ChangesOptions{Cursor: "now"}) {
		n++
		if err == nil {
			t.Fatal("expected missing resume cursor error")
		}
	}
	if calls != 1 || n != 1 {
		t.Fatalf("calls=%d events=%d", calls, n)
	}
}

func TestChangesPreservesFailedErrorResponse(t *testing.T) {
	for _, oversized := range []bool{false, true} {
		t.Run(fmt.Sprint(oversized), func(t *testing.T) {
			db := changesDB(t, func(w http.ResponseWriter, r *http.Request) {
				w.Header().Set("X-Request-ID", "changes-request")
				if !oversized {
					w.Header().Set("Content-Length", "100")
				}
				w.WriteHeader(http.StatusForbidden)
				if oversized {
					fmt.Fprint(w, strings.Repeat("x", 2048))
				} else {
					fmt.Fprint(w, `{"code":"forbidden"}`)
				}
			})
			for _, err := range db.Changes(t.Context(), v1.ChangesOptions{}) {
				var api *v1.APIError
				if !errors.As(err, &api) || api.Status != 403 || api.RequestID != "changes-request" || errors.Unwrap(api) == nil {
					t.Fatalf("error=%#v", err)
				}
				if len(api.Body) == 0 || len(api.Body) > 1024 {
					t.Fatalf("body size=%d", len(api.Body))
				}
			}
		})
	}
}

func TestChangesTerminalError(t *testing.T) {
	calls := 0
	body := `{"error":{"code":"cursor_expired","message":"retention ended"}}`
	db := changesDB(t, func(w http.ResponseWriter, r *http.Request) {
		calls++
		w.Header().Set("Content-Type", "text/event-stream")
		w.Header().Set("X-Request-ID", "terminal-error")
		fmt.Fprint(w, "event: cursor\nid: saved\ndata: {\"cursor\":\"saved\"}\n\nevent: error\ndata: "+body+"\n\n")
	})
	n := 0
	for event, err := range db.Changes(t.Context(), v1.ChangesOptions{}) {
		n++
		if n == 1 {
			if err != nil || event.Cursor != "saved" {
				t.Fatalf("checkpoint=%+v err=%v", event, err)
			}
			continue
		}
		var api *v1.APIError
		if !errors.As(err, &api) || string(api.Code) != "cursor_expired" || api.Status != 200 || api.RequestID != "terminal-error" || string(api.Body) != body {
			t.Fatalf("terminal error=%#v", err)
		}
	}
	if calls != 1 || n != 2 {
		t.Fatalf("calls=%d events=%d", calls, n)
	}
}

func TestChangesRejectsEmptyRecognizedEvent(t *testing.T) {
	for _, kind := range []string{"change", "cursor", "error"} {
		for _, data := range []string{"", "data:\n"} {
			t.Run(kind+data, func(t *testing.T) {
				calls := 0
				db := changesDB(t, func(w http.ResponseWriter, r *http.Request) {
					calls++
					w.Header().Set("Content-Type", "text/event-stream")
					fmt.Fprint(w, "event: "+kind+"\nid: skipped\n"+data+"\nevent: cursor\nid: later\ndata: {\"cursor\":\"later\"}\n\n")
				})
				n := 0
				for _, err := range db.Changes(t.Context(), v1.ChangesOptions{}) {
					n++
					if err == nil {
						t.Fatal("accepted empty event")
					}
				}
				if calls != 1 || n != 1 {
					t.Fatalf("calls=%d events=%d", calls, n)
				}
			})
		}
	}
}

func TestPaginationDetectsMultiCursorCycle(t *testing.T) {
	calls := 0
	db := changesDB(t, func(w http.ResponseWriter, r *http.Request) {
		calls++
		cursor := "A"
		if calls%2 == 0 {
			cursor = "B"
		}
		fmt.Fprintf(w, `{"items":[],"next_cursor":%q}`, cursor)
	})
	errs := 0
	for _, err := range db.Collection("users").Pages(t.Context(), v1.Query{}) {
		if err != nil {
			errs++
		}
	}
	if calls != 3 || errs != 1 {
		t.Fatalf("calls=%d errors=%d", calls, errs)
	}
}

func TestPaginationLongAcyclicSequence(t *testing.T) {
	calls := 0
	db := changesDB(t, func(w http.ResponseWriter, r *http.Request) {
		calls++
		if calls == 100 {
			fmt.Fprint(w, `{"items":[],"next_cursor":null}`)
		} else {
			fmt.Fprintf(w, `{"items":[],"next_cursor":%q}`, fmt.Sprint(calls))
		}
	})
	n := 0
	for _, err := range db.Collection("users").Pages(t.Context(), v1.Query{}) {
		if err != nil {
			t.Fatal(err)
		}
		n++
	}
	if calls != 100 || n != 100 {
		t.Fatalf("calls=%d pages=%d", calls, n)
	}
}

// A commit group checkpoints only its final event; reconnecting partway replays it.
func TestChangesGroupedCheckpoints(t *testing.T) {
	const checkpoint = "event: cursor\nid: before\ndata: {\"cursor\":\"before\"}\n\n"
	change := func(id, cursor string) string {
		header := "event: change\n"
		if cursor != "" {
			header += "id: " + cursor + "\n"
		}
		return header + fmt.Sprintf("data: {\"collection\":\"users\",\"id\":%q,\"op\":\"upsert\",\"version\":\"1\",\"origin\":\"0000000000000001\",\"document\":{}}\n\n", id)
	}
	for _, interrupted := range []bool{false, true} {
		t.Run(fmt.Sprintf("interrupted=%t", interrupted), func(t *testing.T) {
			calls := 0
			db := changesDB(t, func(w http.ResponseWriter, r *http.Request) {
				calls++
				w.Header().Set("Content-Type", "text/event-stream")
				if calls > 2 {
					w.WriteHeader(http.StatusInternalServerError)
					return
				}
				if calls == 1 {
					fmt.Fprint(w, checkpoint+change("01ARZ3NDEKTSV4RRFFQ69G5FD0", ""))
					if interrupted {
						return
					}
				} else {
					want := "after"
					if interrupted {
						want = "before"
						fmt.Fprint(w, change("01ARZ3NDEKTSV4RRFFQ69G5FD0", ""))
					}
					if got := r.Header.Get("Last-Event-ID"); got != want {
						t.Errorf("resume cursor = %q, want %q", got, want)
					}
				}
				fmt.Fprint(w, change("01ARZ3NDEKTSV4RRFFQ69G5FD1", "after"))
			})
			var events []v1.ChangeEvent
			for event, err := range db.Changes(t.Context(), v1.ChangesOptions{}) {
				if err != nil {
					t.Fatal(err)
				}
				events = append(events, event)
				if calls == 2 && event.ID == "01ARZ3NDEKTSV4RRFFQ69G5FD1" {
					break
				}
			}
			if calls != 2 || len(events) != 4 {
				t.Fatalf("calls=%d events=%+v", calls, events)
			}
			for _, event := range events {
				if event.ID == "a" && event.Cursor != "" {
					t.Errorf("uncheckpointed change exposes cursor %q", event.Cursor)
				}
				if event.ID == "01ARZ3NDEKTSV4RRFFQ69G5FD1" && event.Cursor != "after" {
					t.Errorf("group end cursor = %q", event.Cursor)
				}
			}
		})
	}
}

func TestChangesUncheckpointedEOF(t *testing.T) {
	calls := 0
	db := changesDB(t, func(w http.ResponseWriter, r *http.Request) {
		calls++
		w.Header().Set("Content-Type", "text/event-stream")
		fmt.Fprint(w, "event: change\ndata: {\"collection\":\"users\",\"id\":\"01ARZ3NDEKTSV4RRFFQ69G5FD0\",\"op\":\"upsert\",\"version\":\"1\",\"origin\":\"0000000000000001\",\"document\":{}}\n\n")
	})
	events, failures := 0, 0
	for event, err := range db.Changes(t.Context(), v1.ChangesOptions{}) {
		if err != nil {
			failures++
			if !strings.Contains(err.Error(), "without a resume cursor") {
				t.Fatal(err)
			}
		} else {
			events++
			if event.Cursor != "" {
				t.Fatalf("unsafe cursor = %q", event.Cursor)
			}
		}
	}
	if calls != 1 || events != 1 || failures != 1 {
		t.Fatalf("calls=%d events=%d failures=%d", calls, events, failures)
	}
}
