package pitrbench

import (
	"context"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

// putDocs stores documents [lo, hi) at a revision directly in the fake: a restore's state, built by hand.
func putDocs(db *fakeDB, r *Run, lo, hi int64, rev int) {
	db.mu.Lock()
	defer db.mu.Unlock()
	for i := lo; i < hi; i++ {
		db.set(DocID(r.Seed, i), &fakeDoc{live: true, version: fmt.Sprintf("tbv1-hand-%d", i), body: Body(r.Seed, i, rev)})
	}
}

func ledger(t *testing.T, r *Run, phase string, events ...Event) {
	t.Helper()
	if err := os.MkdirAll(filepath.Join(r.Dir, "ledger"), 0o700); err != nil {
		t.Fatal(err)
	}
	for _, e := range events {
		e.Phase = phase
		if err := appendLine(filepath.Join(r.Dir, "ledger", phase+".jsonl"), e); err != nil {
			t.Fatal(err)
		}
	}
}

func verify(t *testing.T, r *Run, cfg VerifyConfig) Verification {
	t.Helper()
	if cfg.Database == "" {
		cfg.Database = r.Database
	}
	if cfg.Workers == 0 {
		cfg.Workers = 8
	}
	if cfg.Versions == "" {
		cfg.Versions = "logical"
	}
	out, err := Verify(context.Background(), r, cfg)
	if err != nil {
		t.Fatal(err)
	}
	return out
}

func wantProblem(t *testing.T, out Verification, want string) {
	t.Helper()
	for _, p := range out.Problems {
		if strings.Contains(p, want) {
			if out.Passed {
				t.Fatal("passed with problems")
			}
			return
		}
	}
	t.Fatalf("problems %q, want one with %q", out.Problems, want)
}

func wantPassed(t *testing.T, out Verification) {
	t.Helper()
	if !out.Passed || out.AcctIndex.Values != AcctValues {
		t.Fatalf("verification failed: %q (acct index %+v)", out.Problems, out.AcctIndex)
	}
}

// A load-a of four batches whose ledger crosses two invocations with different writer counts: invocation A (one
// stream) acknowledged batch 0 and crashed in batch 1; invocation B (two streams) ran batch 2 on stream 0 and
// batches 1 then 3 on stream 1.
func crossInvocationLedger(t *testing.T, r *Run) {
	ledger(t, r, "load-a",
		Event{Batch: 0, Inv: "A", Stream: 0, Ev: EvStart, Ms: 10}, Event{Batch: 0, Inv: "A", Stream: 0, Ev: EvAck, Ms: 20},
		Event{Batch: 1, Inv: "A", Stream: 0, Ev: EvStart, Ms: 21},
		Event{Batch: 2, Inv: "B", Stream: 0, Ev: EvStart, Ms: 200}, Event{Batch: 2, Inv: "B", Stream: 0, Ev: EvAck, Ms: 210},
		Event{Batch: 1, Inv: "B", Stream: 1, Ev: EvStart, Ms: 200}, Event{Batch: 1, Inv: "B", Stream: 1, Ev: EvRetry, Ms: 230},
		Event{Batch: 1, Inv: "B", Stream: 1, Ev: EvStart, Ms: 280}, Event{Batch: 1, Inv: "B", Stream: 1, Ev: EvAck, Ms: 300},
		Event{Batch: 3, Inv: "B", Stream: 1, Ev: EvStart, Ms: 301}, Event{Batch: 3, Inv: "B", Stream: 1, Ev: EvAck, Ms: 310})
}

func TestPrefixAnalysisFollowsInvocationAndStream(t *testing.T) {
	t.Parallel()
	r, db := newTestRun(t, 4000, 4000)
	crossInvocationLedger(t, r)
	// At 250 the restore holds batches 0 and 2. Grouped by the stream of the first event, batch 1 (first sent by A's
	// stream 0) would sit between them as a false gap.
	putDocs(db, r, 0, 1000, 1)
	putDocs(db, r, 2000, 3000, 1)
	if err := r.AddCut(Cut{Name: "b", Kind: "bounded", Partial: "load-a", AtMs: 250}); err != nil {
		t.Fatal(err)
	}
	out := verify(t, r, VerifyConfig{Cut: "b"})
	wantPassed(t, out)
	if b := out.Bounded; b.Present != 2 || b.PrefixGaps != 0 || b.PresentDocs != 2000 {
		t.Fatalf("bounded %+v", b)
	}
	// A real gap: batch 3 present while batch 1, earlier on the same invocation's stream, is not.
	r2, db2 := newTestRun(t, 4000, 4000)
	crossInvocationLedger(t, r2)
	putDocs(db2, r2, 0, 1000, 1)
	putDocs(db2, r2, 3000, 4000, 1)
	_ = r2.AddCut(Cut{Name: "b", Kind: "bounded", Partial: "load-a", AtMs: 400})
	wantProblem(t, verify(t, r2, VerifyConfig{Cut: "b"}), "1 per-stream prefix gaps")
}

func TestBoundedCutOverMutationPhases(t *testing.T) {
	t.Parallel()
	base := func(t *testing.T) (*Run, *fakeDB) {
		r, db := newTestRun(t, testDocs, testSplit)
		for _, p := range []string{"load-a", "load-b", "u1"} {
			write(t, r, p, WriteConfig{})
		}
		return r, db
	}
	t.Run("u1 wholly after the phase", func(t *testing.T) {
		r, _ := newTestRun(t, testDocs, testSplit)
		for _, p := range []string{"load-a", "load-b"} {
			write(t, r, p, WriteConfig{})
		}
		write(t, r, "u1", WriteConfig{})
		if _, err := TakeCut(context.Background(), r, "b", []string{"load-a", "load-b"}, "u1", false, time.Second); err != nil {
			t.Fatal(err)
		}
		out := verify(t, r, VerifyConfig{Cut: "b", Versions: "exact"})
		wantPassed(t, out)
		if b := out.Bounded; b.Present != 1 || b.PresentDocs != 4 {
			t.Fatalf("bounded %+v", b)
		}
	})
	t.Run("d1 before, then present although first sent after the cut", func(t *testing.T) {
		r, _ := base(t)
		if _, err := TakeCut(context.Background(), r, "b", []string{"load-a", "load-b", "u1"}, "d1", false, time.Second); err != nil {
			t.Fatal(err)
		}
		out := verify(t, r, VerifyConfig{Cut: "b", Versions: "exact", Label: "before"})
		wantPassed(t, out)
		if b := out.Bounded; b.Present != 0 || b.PresentDocs != 5 {
			t.Fatalf("bounded before d1 %+v", b)
		}
		time.Sleep(5 * time.Millisecond)
		write(t, r, "d1", WriteConfig{})
		wantProblem(t, verify(t, r, VerifyConfig{Cut: "b", Versions: "exact", Label: "after"}), "batches first sent at or after T not wholly absent")
	})
	t.Run("a d1 batch applied in part", func(t *testing.T) {
		r, db := base(t)
		db.setFault(func(n int) *fault { return &fault{partialApply: 2} })
		write(t, r, "d1", WriteConfig{})
		db.setFault(nil)
		if _, err := TakeCut(context.Background(), r, "b", []string{"load-a", "load-b", "u1"}, "d1", false, time.Second); err != nil {
			t.Fatal(err)
		}
		wantProblem(t, verify(t, r, VerifyConfig{Cut: "b"}), "1 partially applied batches")
	})
}

func TestBoundedInclusionComesFromTheObservedShippedHistory(t *testing.T) {
	t.Parallel()
	setup := func(t *testing.T, present int64) *Run {
		r, db := newTestRun(t, 4000, 4000)
		for b := range int64(4) {
			// Batches 2 and 3 are in flight at the cut (500): A-7 requires only what was acknowledged before it.
			ledger(t, r, "load-a", Event{Batch: b, Inv: "A", Ev: EvStart, Ms: 90 + 100*b}, Event{Batch: b, Inv: "A", Ev: EvAck, Ms: 100 + 100*b + 400*(b/2)})
		}
		putDocs(db, r, 0, present*1000, 1)
		_ = r.AddCut(Cut{Name: "b", Kind: "bounded", Partial: "load-a", AtMs: 500})
		return r
	}
	audit := &Audit{Times: []int64{60, 150, 250, 450, 600}}
	shipped := []int64{150, 250, 450, 600} // second-latest observed before the cut: 250
	out := verify(t, setup(t, 2), VerifyConfig{Cut: "b", Audit: audit, Shipped: shipped})
	wantPassed(t, out)
	if b := out.Bounded; b.IncludeBoundMs != 250 || b.MissingRequired != 0 || out.Shipped.Observed != 4 {
		t.Fatalf("bounded %+v shipped %+v", b, out.Shipped)
	}
	wantProblem(t, verify(t, setup(t, 1), VerifyConfig{Cut: "b", Audit: audit, Shipped: shipped}), "1 required batches missing")
	wantProblem(t, verify(t, setup(t, 2), VerifyConfig{Cut: "b", Audit: &Audit{Times: []int64{150, 450}}, Shipped: shipped}),
		"2 observed shipped reads missing from the bucket")
	// The generation's final segment (600, after the cut) is gone: the audit cannot see it, the shipped history can.
	wantProblem(t, verify(t, setup(t, 2), VerifyConfig{Cut: "b", Audit: &Audit{Times: []int64{60, 150, 250, 450}}, Shipped: shipped}),
		"1 observed shipped reads missing from the bucket (lost segments or snapshots, a generation's final segment included): [600]")
	// Values shipped after the audit are not expected in it.
	wantPassed(t, verify(t, setup(t, 2), VerifyConfig{Cut: "b", Audit: &Audit{Times: []int64{60, 150, 250, 450}, AtMs: 500}, Shipped: shipped}))
	wantProblem(t, verify(t, setup(t, 2), VerifyConfig{Cut: "b", Audit: &Audit{Times: audit.Times, Problems: []string{"gap k: starts at 9"}}, Shipped: shipped}),
		"audit: gap k")
	if _, err := Verify(context.Background(), setup(t, 2), VerifyConfig{Cut: "b", Shipped: shipped, Workers: 1}); err == nil {
		t.Fatal("a shipped history was accepted without the bucket audit")
	}
}

func TestShippedRecorderKeepsEveryDistinctValue(t *testing.T) {
	t.Parallel()
	r, _ := newTestRun(t, testDocs, testSplit)
	ctx, cancel := context.WithCancel(context.Background())
	done := make(chan error, 1)
	go func() { done <- ShippedLoop(ctx, r, "t", 20*time.Millisecond) }()
	write(t, r, "load-a", WriteConfig{Writers: 1})
	write(t, r, "load-b", WriteConfig{Writers: 1})
	time.Sleep(100 * time.Millisecond)
	cancel()
	if err := <-done; err != nil {
		t.Fatal(err)
	}
	values, err := ReadShipped(filepath.Join(r.Dir, "evidence", "shipped-t.jsonl"))
	if err != nil || len(values) < 2 {
		t.Fatalf("history %v (%v): want several values", values, err)
	}
	for k := 1; k < len(values); k++ {
		if values[k] == values[k-1] {
			t.Fatalf("history %v repeats a value", values)
		}
	}
}

func TestResurrectionNamesAPossibleLateHarnessAttempt(t *testing.T) {
	t.Parallel()
	r, db := newTestRun(t, testDocs, testSplit)
	db.setFault(func(n int) *fault {
		if n == 1 {
			return &fault{abortAfter: true} // load-a batch 0: an ambiguous attempt
		}
		return nil
	})
	for _, p := range []string{"load-a", "load-b", "u1", "d1"} {
		write(t, r, p, WriteConfig{Writers: 1})
	}
	if _, err := TakeCut(context.Background(), r, "c", []string{"load-a", "load-b", "u1", "d1"}, "", true, time.Second); err != nil {
		t.Fatal(err)
	}
	putDocs(db, r, 13, 14, 1)     // d1 deleted 13; its load-a batch had an ambiguous attempt
	putDocs(db, r, 2013, 2014, 1) // and 2013, whose load-b batch had none
	out := verify(t, r, VerifyConfig{Cut: "c", Versions: "exact"})
	wantProblem(t, out, "2 resurrected")
	if out.Totals.ResurrectedLate != 1 || len(out.Examples["resurrected_possible_late_attempt"]) != 1 || len(out.Examples["resurrected"]) != 1 {
		t.Fatalf("totals %+v examples %v", out.Totals, out.Examples)
	}
}

func TestVersionDigestsCompareAcrossVerificationsAndEveryAcctIsChecked(t *testing.T) {
	t.Parallel()
	r, db := newTestRun(t, testDocs, testSplit)
	write(t, r, "load-a", WriteConfig{})
	write(t, r, "load-b", WriteConfig{})
	if _, err := TakeCut(context.Background(), r, "c", []string{"load-a", "load-b"}, "", true, time.Second); err != nil {
		t.Fatal(err)
	}
	first := verify(t, r, VerifyConfig{Cut: "c", Label: "first"})
	wantPassed(t, first)
	prev := filepath.Join(r.Dir, "evidence", "verify-pitr-c-first.json")
	wantPassed(t, verify(t, r, VerifyConfig{Cut: "c", Compare: prev, Label: "again"}))
	db.mu.Lock()
	db.docs[DocID(r.Seed, 2500)].version = "tbv1-another-version" // same content, another version: a restart changed it
	db.mu.Unlock()
	wantProblem(t, verify(t, r, VerifyConfig{Cut: "c", Compare: prev, Label: "changed"}), "1 partition version digests differ")
	db.mu.Lock()
	db.indexHides[DocID(r.Seed, 3999)] = true
	db.mu.Unlock()
	out := verify(t, r, VerifyConfig{Cut: "c", Label: "index"})
	wantProblem(t, out, "acct index: 20011 of 20011 values queried, 1 differ")
	if out.AcctIndex.Missing != 1 || out.AcctIndex.Documents != testDocs-1 {
		t.Fatalf("acct index %+v", out.AcctIndex)
	}
}

func TestReconcileGuardsEveryRestart(t *testing.T) {
	t.Parallel()
	r, db := newTestRun(t, testDocs, testSplit)
	write(t, r, "load-a", WriteConfig{})
	restart := func() {
		n, _ := LoadNode(r)
		n.Starts++
		if err := writeJSON(filepath.Join(r.Dir, "node.json"), n); err != nil {
			t.Fatal(err)
		}
	}
	restart()
	restart() // start 2
	ctx := context.Background()
	if _, err := Write(ctx, r, WriteConfig{Phase: "load-b", Writers: 1}); err == nil || !strings.Contains(err.Error(), "reconcile") {
		t.Fatalf("a write after a restart: %v", err)
	}
	if _, err := TakeCut(ctx, r, "c", []string{"load-a"}, "", true, time.Second); err == nil || !strings.Contains(err.Error(), "reconcile") {
		t.Fatalf("a cut after a restart: %v", err)
	}
	if out, err := Reconcile(ctx, r, 8); err != nil || !out.Passed {
		t.Fatalf("reconcile: %v %q", err, out.Problems)
	}
	write(t, r, "load-b", WriteConfig{Writers: 1, MaxBatches: 1}) // allowed again; load-b batch 0 acknowledged, the phase in progress
	restart()
	db.mu.Lock()
	for i := int64(2000); i < 3000; i++ {
		id := DocID(r.Seed, i) // an acknowledged load-b transaction lost whole by an unclean crash
		delete(db.byAcct[db.docs[id].acct], id)
		delete(db.docs, id)
	}
	delete(db.other["fences"], fenceID("load-a"))
	db.mu.Unlock()
	out, err := Reconcile(ctx, r, 8)
	if err != nil {
		t.Fatal(err)
	}
	wantProblem(t, out, "possible local-durability loss")
	wantProblem(t, out, "the fence of load-a was proven but does not read back")
	wantProblem(t, out, "1 required batches missing")
	if _, err := os.Stat(filepath.Join(r.Dir, "reconciled.json")); err != nil {
		t.Fatal(err)
	}
	if _, err := Write(ctx, r, WriteConfig{Phase: "load-b", Writers: 1}); err == nil || !strings.Contains(err.Error(), "reconcile") {
		t.Fatalf("a write after a failed reconcile: %v", err)
	}
}

func TestMarkersBetweenTheirSendAndTheirCutAreOptional(t *testing.T) {
	t.Parallel()
	r, db := newTestRun(t, 4000, 4000)
	// Exact cut e: marker sent at 500, cut at 1000. Bounded cuts before the marker (400), between (700), after (1100).
	_ = r.AddCut(Cut{Name: "e", Kind: "exact", AtMs: 1000, MarkerMs: 500})
	for name, at := range map[string]int64{"before": 400, "between": 700, "after": 1100} {
		_ = r.AddCut(Cut{Name: name, Kind: "bounded", Partial: "load-a", AtMs: at})
	}
	check := func(cut string, present, pass bool) {
		t.Helper()
		db.mu.Lock()
		if present {
			db.coll("markers")["e"] = &fakeDoc{live: true, version: "m", body: []byte(`{}`)}
		} else {
			delete(db.coll("markers"), "e")
		}
		db.mu.Unlock()
		if out := verify(t, r, VerifyConfig{Cut: cut}); out.Markers.Passed != pass {
			t.Fatalf("cut %s, marker present %v: markers %+v, want passed %v", cut, present, out.Markers, pass)
		}
	}
	check("before", false, true)
	check("before", true, false) // sent after the cut: never in a restore at it
	check("between", false, true)
	check("between", true, true) // sent before the cut, committed at an unknown point: either
	check("after", true, true)
	check("after", false, false) // its cut is at or before this one: required
	// A cut recorded before marker_ms: the send time comes from its note.
	_ = r.AddCut(Cut{Name: "old", Kind: "exact", AtMs: 2000, Note: "marker written at 1500"})
	_ = r.AddCut(Cut{Name: "mid", Kind: "bounded", Partial: "load-a", AtMs: 1700})
	db.mu.Lock()
	db.coll("markers")["old"] = &fakeDoc{live: true, version: "m", body: []byte(`{}`)}
	db.mu.Unlock()
	check("mid", true, true)
}
