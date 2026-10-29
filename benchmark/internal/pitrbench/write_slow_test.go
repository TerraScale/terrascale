//go:build slow

package pitrbench

import (
	"context"
	"errors"
	"strings"
	"terrascale.tech/ts-benchmark/internal/benchclient"
	"testing"
	"time"
)

func TestSlowDelayedCommitOfATimedOutAttemptAppliesOnce(t *testing.T) {
	t.Parallel()
	for _, late := range []string{"first attempt commits before the retry", "retry commits before the first attempt"} {
		t.Run(late, func(t *testing.T) {
			r, db := newTestRun(t, testDocs, testSplit)
			hold, evaluated := make(chan struct{}), make(chan int, 1)
			db.setFault(func(n int) *fault {
				switch {
				case n == 1:
					return &fault{hold: hold, evaluated: evaluated}
				case n == 2 && late == "first attempt commits before the retry":
					close(hold)
					<-evaluated
				}
				return nil
			})
			stats := write(t, r, "load-a", WriteConfig{Writers: 1, Timeout: time.Second})
			if late == "retry commits before the first attempt" {
				close(hold)
				if status := <-evaluated; status != 412 {
					t.Fatalf("the delayed first attempt was evaluated with %d after the retry applied, want 412", status)
				}
			}
			checkExactlyOnce(t, r, db, "load-a")
			checkPhase(t, r, db, "load-a")
			if stats.Attempted != 2 || stats.AckedDocs != testSplit || stats.Ambiguous < 1 || stats.Acked+stats.Present != 2 {
				t.Fatalf("stats %+v: want 2 batches proven once each, %d documents, an ambiguous outcome", stats, testSplit)
			}
		})
	}
}

func TestSlowLostAcknowledgementIsProvenNotAssumed(t *testing.T) {
	t.Parallel()
	for _, mode := range []string{"stream reset", "500 after commit"} {
		t.Run(mode, func(t *testing.T) {
			r, db := newTestRun(t, testDocs, testSplit)
			db.setFault(func(n int) *fault {
				if n == 1 {
					return &fault{abortAfter: mode == "stream reset", failAfter: mode == "500 after commit"}
				}
				return nil
			})
			stats := write(t, r, "load-a", WriteConfig{Writers: 1})
			checkExactlyOnce(t, r, db, "load-a")
			checkPhase(t, r, db, "load-a")
			if stats.Present != 1 || stats.Acked != 1 {
				t.Fatalf("stats %+v: want the first batch proven by read and the second acknowledged", stats)
			}
		})
	}
}

func TestSlowDuplicatedRetriesNeverInflateCounts(t *testing.T) {
	t.Parallel()
	r, db := newTestRun(t, testDocs, testSplit)
	holds := []chan struct{}{make(chan struct{}), make(chan struct{})}
	evaluated := make(chan int, 2)
	db.setFault(func(n int) *fault {
		if n <= 2 {
			return &fault{hold: holds[n-1], evaluated: evaluated}
		}
		return nil
	})
	stats := write(t, r, "load-a", WriteConfig{Writers: 1, Timeout: time.Second})
	for _, h := range holds {
		close(h)
		if status := <-evaluated; status != 412 {
			t.Fatalf("a stale duplicate was evaluated with %d, want 412", status)
		}
	}
	checkExactlyOnce(t, r, db, "load-a")
	checkPhase(t, r, db, "load-a")

	if stats.Attempted != 2 || stats.Acked+stats.Present != 2 || stats.Attempts < 4 || stats.Retries != stats.Attempts-2 || stats.AckedDocs != testSplit {
		t.Fatalf("stats %+v: want 2 batches proven once each, at least 4 attempts, %d documents", stats, testSplit)
	}
}

func TestSlowDeleteAndRecreateTrackTombstoneVersions(t *testing.T) {
	t.Parallel()
	r, db := newTestRun(t, testDocs, testSplit)
	for _, p := range []string{"load-a", "load-b", "u1"} {
		write(t, r, p, WriteConfig{})
	}

	db.setFault(func(n int) *fault { return &fault{abortAfter: true, afterCommit: func() { db.fault = nil }} })
	if stats := write(t, r, "d1", WriteConfig{}); stats.Present != 1 {
		t.Fatalf("d1 stats %+v: want its batch proven by read", stats)
	}
	checkPhase(t, r, db, "d1")
	write(t, r, "r1", WriteConfig{})
	checkPhase(t, r, db, "r1")
	checkExactlyOnce(t, r, db, "load-a", "load-b", "u1", "d1", "r1")
	o, _ := NewOracle(r, []string{"load-a", "load-b", "u1", "d1", "r1"})
	db.mu.Lock()
	defer db.mu.Unlock()
	for i := range r.Docs {
		e, d := o.At(i), db.docs[DocID(r.Seed, i)]
		if e.Alive != d.live || (e.Alive && string(d.body) != string(Body(r.Seed, i, e.Rev))) {
			t.Fatalf("document %d: database %+v, oracle %+v", i, d, e)
		}
	}
}

func TestSlowRecreateCannotHideAMissedDelete(t *testing.T) {
	t.Parallel()
	r, db := newTestRun(t, testDocs, testSplit)
	for _, p := range []string{"load-a", "load-b", "u1"} {
		write(t, r, p, WriteConfig{})
	}
	db.mu.Lock()
	db.dropDelete[DocID(r.Seed, 13)] = true
	db.mu.Unlock()
	write(t, r, "d1", WriteConfig{})
	_, err := Write(context.Background(), r, WriteConfig{Phase: "r1", Writers: 1, Timeout: 2 * time.Second})
	if err == nil || !strings.Contains(err.Error(), "unresolved") {
		t.Fatalf("re-creating over a missed delete: %v, want unresolved", err)
	}
	states, _ := ReadLedger(r, "r1")
	if st := states[0]; st == nil || st.Terminal != "" || !st.Flagged {
		t.Fatalf("r1 batch 0 ledger state %+v: want no terminal event and an unresolved flag", st)
	}
	db.mu.Lock()
	defer db.mu.Unlock()
	if got := db.applied[DocID(r.Seed, 13)]; got != 1 {
		t.Fatalf("document 13 applied %d writes, want 1 (its load; the delete was dropped): the re-create must not apply", got)
	}
}

func TestSlowAbsentWithoutTombstoneIsNotProofOfDelete(t *testing.T) {
	t.Parallel()
	r, db := newTestRun(t, testDocs, testSplit)
	for _, p := range []string{"load-a", "load-b", "u1"} {
		write(t, r, p, WriteConfig{})
	}

	db.setFault(func(n int) *fault {
		return &fault{abortAfter: true, afterCommit: func() {
			db.fault = nil
			for _, i := range []int64{13, 1013, 2013, 3013, 7} {
				delete(db.docs, DocID(r.Seed, i))
			}
		}}
	})
	_, err := Write(context.Background(), r, WriteConfig{Phase: "d1", Writers: 1, Timeout: 2 * time.Second})
	if err == nil || !strings.Contains(err.Error(), "404 without a tombstone version") {
		t.Fatalf("d1 over vanished documents: %v, want unresolved for lack of tombstone proof", err)
	}
	if p, _ := r.Phase("d1"); phaseComplete(r, p) == nil {
		t.Fatal("d1 counts as complete without proof")
	}
}

func TestSlowCrashesAroundIntentCommitAcknowledgementAndCompletionResume(t *testing.T) {
	t.Parallel()
	for _, point := range []string{"after-intent", "after-commit", "after-response", "after-versions"} {
		t.Run(point, func(t *testing.T) {
			r, db := newTestRun(t, testDocs, testSplit)
			ctx, cancel := context.WithCancel(context.Background())
			defer cancel()
			cfg := WriteConfig{Phase: "load-a", Writers: 1, Timeout: 2 * time.Second}
			crashed := false
			if point == "after-commit" {

				db.setFault(func(n int) *fault {
					if n == 2 {
						return &fault{afterCommit: func() { cancel(); time.Sleep(100 * time.Millisecond) }}
					}
					return nil
				})
			} else {
				cfg.hook = func(at string, b int64) error {
					if at == point && b == 1 && !crashed {
						crashed = true
						return errCrash
					}
					return nil
				}
			}
			_, err := Write(ctx, r, cfg)
			if point != "after-commit" && !errors.Is(err, errCrash) {
				t.Fatalf("crash run: %v", err)
			}
			states, _ := ReadLedger(r, "load-a")
			if st := states[1]; st == nil || st.Terminal != "" {
				t.Fatalf("after a crash %s the batch must be unknown, got %+v", point, st)
			}
			if err := phaseComplete(r, Phase{Name: "load-a", lo: 0, hi: testSplit}); err == nil {
				t.Fatal("phase complete with an unknown batch")
			}
			db.setFault(nil)
			stats := write(t, r, "load-a", WriteConfig{Writers: 1})
			wantPresent := int64(1)
			if point == "after-intent" {
				wantPresent = 0
			}
			if stats.Present != wantPresent || stats.Skipped != 1 {
				t.Fatalf("resume stats %+v: want %d proven by read and 1 skipped", stats, wantPresent)
			}
			checkExactlyOnce(t, r, db, "load-a")
			checkPhase(t, r, db, "load-a")
		})
	}
}

func TestSlowExactCutPinsTheLedgerPrefixAndRefusesUnresolvedBatches(t *testing.T) {
	t.Parallel()
	r, db := newTestRun(t, testDocs, testSplit)
	cfg := WriteConfig{Phase: "load-a", Writers: 1, Timeout: 2 * time.Second, hook: func(at string, b int64) error {
		if at == "after-response" && b == 1 {
			return errCrash
		}
		return nil
	}}
	if _, err := Write(context.Background(), r, cfg); !errors.Is(err, errCrash) {
		t.Fatalf("crash run: %v", err)
	}
	if _, err := TakeCut(context.Background(), r, "c-unresolved", []string{"load-a"}, "", false, time.Second); err == nil {
		t.Fatal("an exact cut was taken over an unresolved batch")
	}
	write(t, r, "load-a", WriteConfig{Writers: 1})
	cut, err := TakeCut(context.Background(), r, "c1", []string{"load-a"}, "", false, 5*time.Second)
	if err != nil {
		t.Fatal(err)
	}
	lines, _ := LedgerLines(r, "load-a")
	if cut.LedgerLines["load-a"] != lines || lines == 0 {
		t.Fatalf("cut pins %d ledger lines, ledger has %d", cut.LedgerLines["load-a"], lines)
	}
	if cut.AtMs < cut.LastAckMs+1000 {
		t.Fatalf("cut at %d is not after the last acknowledgement %d plus the marker's second", cut.AtMs, cut.LastAckMs)
	}
	_ = db
}

func TestSlowUnshippedCutVerifiesTheSourceButIsNeverRestorable(t *testing.T) {
	t.Parallel()
	r, db := newTestRun(t, testDocs, testSplit)
	db.noBackup = true
	write(t, r, "load-a", WriteConfig{})
	if _, err := TakeCut(context.Background(), r, "exact", []string{"load-a"}, "", false, time.Second); err == nil {
		t.Fatal("an exact cut was taken on a node that ships nothing")
	}
	if _, err := TakeCut(context.Background(), r, "u", []string{"load-a"}, "load-b", true, time.Second); err == nil {
		t.Fatal("an unshipped cut took a partial phase")
	}
	cut, err := TakeCut(context.Background(), r, "u", []string{"load-a"}, "", true, time.Second)
	if err != nil || cut.Kind != "unshipped" || cut.AtMs < cut.LastAckMs+1000 {
		t.Fatalf("unshipped cut %+v: %v", cut, err)
	}
	if _, err := Restore(context.Background(), r, nil, "u", cut.AtMs, "restored", "source"); err == nil || !strings.Contains(err.Error(), "cannot be restored") {
		t.Fatalf("restore of an unshipped cut: %v", err)
	}
}

func TestSlowIdsAreUniqueOrderedAndBodiesCanonical(t *testing.T) {
	t.Parallel()
	const seed = 7
	prev := ""
	for i := range int64(20_000) {
		id := DocID(seed, i)
		if id <= prev {
			t.Fatalf("id %d %q does not sort after %q", i, id, prev)
		}
		if j, ok := IndexOf(seed, id); !ok || j != i {
			t.Fatalf("IndexOf(%q) = %d, %v", id, j, ok)
		}
		prev = id
		for _, rev := range []int{1, 2, 3} {
			b := Body(seed, i, rev)
			c, err := benchclient.Canonical(b)
			if err != nil || string(c) != string(b) {
				t.Fatalf("body %d rev %d is not canonical: %s", i, rev, b)
			}
		}
	}
	if _, ok := IndexOf(seed+1, DocID(seed, 5)); ok {
		t.Fatal("another seed's id was accepted")
	}
}

func TestSlowOracleAtTheCutsAroundMutations(t *testing.T) {
	t.Parallel()
	r := &Run{Docs: 100_000, LoadSplit: 40_000}
	count := func(applied ...string) (alive, deleted int) {
		o, err := NewOracle(r, applied)
		if err != nil {
			t.Fatal(err)
		}
		for i := range r.Docs {
			e := o.At(i)
			if e.Alive {
				alive++
			}
			if e.Deleted {
				deleted++
			}
		}
		return
	}
	for _, c := range []struct {
		applied        []string
		alive, deleted int
	}{
		{[]string{"load-a"}, 40_000, 0},
		{[]string{"load-a", "load-b", "u1"}, 100_000, 0},
		{[]string{"load-a", "load-b", "u1", "d1"}, 100_000 - 110, 110},
		{[]string{"load-a", "load-b", "u1", "d1", "r1"}, 100_000 - 100, 100},
	} {
		if alive, deleted := count(c.applied...); alive != c.alive || deleted != c.deleted {
			t.Fatalf("%v: %d alive %d deleted, want %d and %d", c.applied, alive, deleted, c.alive, c.deleted)
		}
	}
	o, _ := NewOracle(r, []string{"load-a", "load-b", "u1", "d1", "r1"})
	if e := o.At(7); !e.Deleted || e.Phase != "d1" {
		t.Fatalf("document 7 (updated then deleted): %+v", e)
	}
	if e := o.At(13); !e.Alive || e.Rev != 3 || e.Phase != "r1" {
		t.Fatalf("document 13 (deleted then re-created): %+v", e)
	}
	if e := o.At(1007); !e.Alive || e.Rev != 2 || e.Phase != "u1" {
		t.Fatalf("document 1007 (updated): %+v", e)
	}
}

func TestSlowAnInterruptedWriteFailsAndIsNotFenced(t *testing.T) {
	t.Parallel()
	r, db := newTestRun(t, testDocs, testSplit)
	ctx, cancel := context.WithCancel(context.Background())
	db.setFault(func(n int) *fault {
		if n == 2 {
			cancel()
		}
		return nil
	})
	stats, err := Write(ctx, r, WriteConfig{Phase: "load-a", Writers: 1, Timeout: 2 * time.Second})
	if err == nil || !strings.Contains(err.Error(), "interrupted") || stats.Fenced || stats.StoppedFor != "interrupted" {
		t.Fatalf("interrupted write: %v, stats %+v", err, stats)
	}
}
