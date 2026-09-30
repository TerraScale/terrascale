package sdk

import (
	"encoding/json"
	"net/http"
	"testing"
)

func TestNativeSubscriptionBindsPredicateAndPreservesAtomicLeave(t *testing.T) {
	value := I64Value(7)
	predicate := QueryPredicate{Operation: QueryEqual, Path: []uint32{1}, Value: &value}
	calls := 0
	client, _ := nativeTestClient(t, func(w http.ResponseWriter, r *http.Request) {
		calls++
		if r.Method != "POST" || r.URL.Path != nativeTestScope.path()+"/collections/"+string(nativeTestCollection)+"/documents/subscriptions" {
			t.Error("subscription scope drift")
		}
		var body struct {
			Phase          string         `json:"phase"`
			Predicate      QueryPredicate `json:"predicate"`
			SnapshotCursor string         `json:"snapshot_cursor"`
			PollCursor     string         `json:"poll_cursor"`
		}
		if err := json.NewDecoder(r.Body).Decode(&body); err != nil {
			t.Error(err)
		}
		if value, ok := body.Predicate.Value.Int64(); !ok || value != 7 || body.Predicate.Path[0] != 1 {
			t.Error("subscription predicate was borrowed")
		}
		w.Header().Set("Content-Type", "application/json")
		switch calls {
		case 1:
			if body.Phase != "snapshot" {
				t.Error("wrong first phase")
			}
			_, _ = w.Write([]byte(`{"version":1,"phase":"snapshot","rows":[],"poll_cursor":"AQ"}`))
		case 2:
			if body.Phase != "poll" || body.PollCursor != "AQ" {
				t.Error("wrong poll continuation")
			}
			_, _ = w.Write([]byte(`{"version":1,"phase":"poll","groups":[{"sequence":"1","effects":[{"document":"44444444-4444-4444-4444-444444444444","point":null}]}],"poll_cursor":"Ag"}`))
		case 3:
			_, _ = w.Write([]byte(`{"version":1,"phase":"poll","groups":[{"sequence":"18446744073709551615","effects":[]}],"poll_cursor":"Aw"}`))
		default:
			t.Error("unexpected subscription replay")
		}
	})
	subscription, err := client.Documents.Subscribe(nativeTestScope, nativeTestCollection, predicate, 128)
	if err != nil {
		t.Fatal(err)
	}
	predicate.Path[0] = 9
	*predicate.Value = I64Value(8)
	first, err := subscription.Next(t.Context())
	if err != nil || first.Snapshot == nil {
		t.Fatalf("snapshot failed: %v", err)
	}
	second, err := subscription.Next(t.Context())
	if err != nil || second.Poll == nil {
		t.Fatalf("poll failed: %v", err)
	}
	if len(second.Poll.Groups) != 1 || len(second.Poll.Groups[0].Effects) != 1 || second.Poll.Groups[0].Effects[0].Point != nil {
		t.Fatal("atomic leave effect lost")
	}
	if _, err := subscription.Next(t.Context()); err != nil {
		t.Fatal(err)
	}
	if subscription.PollCursor() != "Aw" || calls != 3 {
		t.Fatal("poll progress lost")
	}
}

func TestNativeSubscriptionRejectsStalledOrMalformedCursor(t *testing.T) {
	predicate := QueryPredicate{Operation: QueryIsMissing, Path: []uint32{1}}
	for _, body := range []string{`{"version":1,"phase":"snapshot","rows":[],"next_snapshot_cursor":"AQ","poll_cursor":"Ag"}`, `{"version":1,"phase":"snapshot","rows":[],"next_snapshot_cursor":"AQ"}`, `{"version":1,"phase":"snapshot","rows":[],"poll_cursor":""}`, `{"version":1,"phase":"snapshot","rows":[],"poll_cursor":"AQ=="}`} {
		client, _ := nativeTestClient(t, func(w http.ResponseWriter, r *http.Request) {
			w.Header().Set("Content-Type", "application/json")
			_, _ = w.Write([]byte(body))
		})
		if _, err := client.Documents.SubscriptionSnapshot(t.Context(), nativeTestScope, nativeTestCollection, SubscriptionSnapshotRequest{Version: 1, Phase: "snapshot", Predicate: predicate, Limit: 1, SnapshotCursor: "AQ"}); err == nil {
			t.Errorf("bad snapshot accepted: %s", body)
		}
	}
	client, _ := nativeTestClient(t, func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write([]byte(`{"version":1,"phase":"poll","groups":[{"sequence":"1","effects":[{"document":"44444444-4444-4444-4444-444444444444","point":null}]}],"poll_cursor":"AQ"}`))
	})
	if _, err := client.Documents.SubscriptionPoll(t.Context(), nativeTestScope, nativeTestCollection, SubscriptionPollRequest{Version: 1, Phase: "poll", Predicate: predicate, PollCursor: "AQ"}); err == nil {
		t.Fatal("stalled poll cursor accepted")
	}
}
