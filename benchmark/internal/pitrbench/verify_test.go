package pitrbench

import (
	"context"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"testing"
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
