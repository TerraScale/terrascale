//go:build slow

package pitrbench

import (
	"context"
	"path/filepath"
	"testing"
	"time"
)

func TestSlowA7AcknowledgedBeforeTMustBeRestored(t *testing.T) {
	t.Parallel()
	out := verify(t, a7Run(t, nil, 0, 1), VerifyConfig{Cut: "t"})
	wantPassed(t, out)
	if a := out.A7; a.AckedBefore != 2 || a.InFlight != 1 || a.SentAfter != 1 || a.InFlightPresent != 0 {
		t.Fatalf("a7 %+v", a)
	}
	out = verify(t, a7Run(t, nil, 0), VerifyConfig{Cut: "t"})
	wantProblem(t, out, "A-7: 1 batches acknowledged before T not wholly present: [1]")
	// A remote node's clock allowance: an acknowledgement within the skew of T is in flight, so either is allowed.
	out = verify(t, a7Run(t, nil, 0), VerifyConfig{Cut: "t", SkewMs: 150})
	wantPassed(t, out)
	if a := out.A7; a.AckedBefore != 1 || a.InFlight != 3 || a.SentAfter != 0 {
		t.Fatalf("a7 with skew %+v", a)
	}
}

func TestSlowA7SentAtOrAfterTMustBeAbsent(t *testing.T) {
	t.Parallel()
	wantProblem(t, verify(t, a7Run(t, nil, 0, 1, 3), VerifyConfig{Cut: "t"}), "A-7: 1 batches first sent at or after T not wholly absent: [3]")
	// Never sent at all, yet present.
	r := a7Run(t, map[int64][]Event{3: nil}, 0, 1, 3)
	wantProblem(t, verify(t, r, VerifyConfig{Cut: "t"}), "A-7: 1 batches first sent at or after T not wholly absent: [3]")
}

func TestSlowA7InFlightBatchesMayGoEitherWay(t *testing.T) {
	t.Parallel()
	// Batch 2 committed late (acknowledged after T), or lost its acknowledgement: ambiguous before T, retried, proven by
	// read after T. Either way it was in flight at T.
	for name, lines := range map[string][]Event{
		"late commit": nil,
		"lost acknowledgement": {{Stream: 0, Ev: EvStart, Ms: 950}, {Stream: 0, Ev: EvRetry, Ms: 990}, {Stream: 0, Ev: EvStart, Ms: 1010},
			{Stream: 0, Ev: EvPresent, Ms: 1250, Status: 412}},
	} {
		t.Run(name, func(t *testing.T) {
			events := map[int64][]Event{}
			if lines != nil {
				events[2] = lines
			}
			absent := verify(t, a7Run(t, events, 0, 1), VerifyConfig{Cut: "t"})
			wantPassed(t, absent)
			present := verify(t, a7Run(t, events, 0, 1, 2), VerifyConfig{Cut: "t"})
			wantPassed(t, present)
			if present.A7.InFlightPresent != 1 || absent.A7.InFlightPresent != 0 {
				t.Fatalf("in flight present: %+v / %+v", present.A7, absent.A7)
			}
		})
	}
	// In flight, but not as a per-stream prefix: batch 2 present while batch 0, earlier on stream 0, is absent.
	wantProblem(t, verify(t, a7Run(t, map[int64][]Event{0: {{Stream: 0, Ev: EvStart, Ms: 300}, {Stream: 0, Ev: EvAck, Ms: 1100}}}, 1, 2),
		VerifyConfig{Cut: "t"}), "1 per-stream prefix gaps")
}

func TestSlowA7UnresolvedBatchesAreNeverCountedAsWritten(t *testing.T) {
	t.Parallel()
	unresolved := func(start int64) []Event {
		return []Event{{Stream: 1, Ev: EvStart, Ms: start}, {Stream: 1, Ev: EvRetry, Ms: start + 50}, {Stream: 1, Ev: EvUnresolved, Ms: start + 70}}
	}
	// Batch 1 sent before T with no proven outcome: never required, whether the restore holds it or not.
	for _, present := range [][]int64{{0}, {0, 1}} {
		out := verify(t, a7Run(t, map[int64][]Event{1: unresolved(800)}, present...), VerifyConfig{Cut: "t"})
		wantPassed(t, out)
		if a := out.A7; a.AckedBefore != 1 || a.Unresolved != 1 || a.InFlight != 2 {
			t.Fatalf("restore holding %v: a7 %+v", present, a)
		}
	}
	// Unresolved, first sent at T, and present: the batch was sent after T, whatever its outcome.
	wantProblem(t, verify(t, a7Run(t, map[int64][]Event{3: unresolved(1000)}, 0, 1, 3), VerifyConfig{Cut: "t"}),
		"A-7: 1 batches first sent at or after T not wholly absent: [3]")
}

func TestSlowA7ExactCutsNeedTheLedgerToAgree(t *testing.T) {
	t.Parallel()
	r, db := newTestRun(t, 4000, 2000)
	ledger(t, r, "load-a", Event{Batch: 0, Ev: EvStart, Ms: 100}, Event{Batch: 0, Ev: EvAck, Ms: 200},
		Event{Batch: 1, Ev: EvStart, Ms: 300}, Event{Batch: 1, Ev: EvAck, Ms: 900})
	ledger(t, r, "load-b", Event{Batch: 0, Ev: EvStart, Ms: 2100})
	putDocs(db, r, 0, 2000, 1)
	db.mu.Lock()
	db.coll("markers")["e"] = &fakeDoc{live: true, version: "m", body: []byte(`{}`)}
	db.mu.Unlock()
	_ = r.AddCut(Cut{Name: "e", Kind: "unshipped", Applied: []string{"load-a"}, AtMs: 1000, MarkerMs: 950})
	out := verify(t, r, VerifyConfig{Cut: "e"})
	wantPassed(t, out)
	if a := out.A7; a.AckedBefore != 2 || a.SentAfter != 1 || a.Conflicts != 0 {
		t.Fatalf("a7 %+v", a)
	}
	// With 150 ms of skew, load-a batch 1 (900) is no longer acknowledged before T: the exact oracle overstates A-7.
	wantProblem(t, verify(t, r, VerifyConfig{Cut: "e", SkewMs: 150}), "A-7: 1 ledger times contradict the cut with skew 150 ms")
	// A later phase sent before T: the cut was not quiesced at T.
	ledger(t, r, "load-b", Event{Batch: 1, Ev: EvStart, Ms: 999})
	wantProblem(t, verify(t, r, VerifyConfig{Cut: "e"}), "A-7: 1 ledger times contradict the cut with skew 0 ms")
}

func TestSlowATypedRefusalIsRecordedAsRefused(t *testing.T) {
	t.Parallel()
	r, db := newTestRun(t, 4000, 4000)
	_ = r.AddCut(Cut{Name: "t", Kind: "bounded", Partial: "load-a", AtMs: 1500})
	c, err := r.Client(time.Second)
	if err != nil {
		t.Fatal(err)
	}
	for k, tc := range []struct {
		status  int
		body    string
		outcome string
		fails   bool
	}{
		{409, `{"error":{"code":"restore_point_unproven","message":"m"}}`, "refused:restore_point_unproven", false},
		{409, `{"error":{"code":"backup_history_incomplete","message":"m"}}`, "refused:backup_history_incomplete", false},
		{409, `{"error":{"code":"name_taken","message":"m"}}`, OutcomeFailed, true},
		{500, `{"error":{"code":"internal_error","message":"m"}}`, OutcomeFailed, true},
		{201, `{"name":"x"}`, OutcomeRestored, false},
	} {
		db.mu.Lock()
		db.restoreStatus, db.restoreBody = tc.status, tc.body
		db.mu.Unlock()
		name := "x" + string(rune('a'+k))
		res, err := Restore(context.Background(), r, c, "t", 1500, name, "source")
		var saved RestoreResult
		if rerr := readJSON(filepath.Join(r.Dir, "evidence", "restore-"+name+".json"), &saved); rerr != nil || saved.Outcome != tc.outcome {
			t.Fatalf("HTTP %d: evidence outcome %q (%v), want %q", tc.status, saved.Outcome, rerr, tc.outcome)
		}
		// A refusal is its own outcome: not restored (nothing to pass), not an error (no loss implied).
		if res.Outcome != tc.outcome || (err != nil) != tc.fails {
			t.Fatalf("HTTP %d: outcome %q error %v, want %q failing %v", tc.status, res.Outcome, err, tc.outcome, tc.fails)
		}
	}
}

func TestSlowWindowLatestIsTheDisasterRecoveryPoint(t *testing.T) {
	t.Parallel()
	r, db := newTestRun(t, 4000, 4000)
	db.window = `{"earliest":"2026-10-13T03:00:40.789+00:00","latest":"2026-10-13T03:02:05+00:00"}`
	c, err := r.Client(time.Second)
	if err != nil {
		t.Fatal(err)
	}
	if at, err := WindowLatest(context.Background(), r, c); err != nil || at != 1791860525000 {
		t.Fatalf("window latest %d: %v", at, err)
	}
}
