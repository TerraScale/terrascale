//go:build slow

package pitrbench

import (
	"context"
	"errors"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

func TestSlowExactCutWaitsForAPassThatBeganAfterTheMarker(t *testing.T) {
	t.Parallel()
	t.Run("a pass ends as the marker commits, without it; later passes ship it", func(t *testing.T) {
		r, db := newTestRun(t, testDocs, testSplit)
		write(t, r, "load-a", WriteConfig{})
		db.mu.Lock()
		db.racyMarker = true
		db.mu.Unlock()
		cut, err := TakeCut(context.Background(), r, "c1", []string{"load-a"}, "", false, 10*time.Second)
		if err != nil {
			t.Fatal(err)
		}
		db.mu.Lock()
		marker, shipped := db.other["markers"]["c1"].write, db.shippedSeq
		db.mu.Unlock()
		if shipped < marker || cut.ShippedMs == cut.ShippedV1 || cut.ShippedV1 == cut.ShippedV0 {
			t.Fatalf("cut %+v completed with the bucket at write %d, the marker is write %d", cut, shipped, marker)
		}
	})
	t.Run("the racing pass is the last one: the cut must not complete", func(t *testing.T) {
		r, db := newTestRun(t, testDocs, testSplit)
		write(t, r, "load-a", WriteConfig{})
		db.mu.Lock()
		db.racyMarker, db.stopAtMarker = true, true
		db.mu.Unlock()
		// A LastShippedAt at or after the marker's request with nothing pending: the old quiesce accepted exactly this.
		if _, err := TakeCut(context.Background(), r, "c1", []string{"load-a"}, "", false, 2*time.Second); err == nil || !strings.Contains(err.Error(), "finish a pass after the marker") {
			t.Fatalf("cut over a marker no pass read: %v", err)
		}
		if _, err := r.Cut("c1"); err == nil {
			t.Fatal("the failed cut was recorded")
		}
	})
}

func TestSlowExactCutExtendsTheRestoreWindowPastTheCut(t *testing.T) {
	t.Parallel()
	r, db := newTestRun(t, testDocs, testSplit)
	write(t, r, "load-a", WriteConfig{})
	cut, err := TakeCut(context.Background(), r, "c1", []string{"load-a"}, "", false, 10*time.Second)
	if err != nil {
		t.Fatal(err)
	}
	db.mu.Lock()
	last := db.nudges[len(db.nudges)-1]
	db.mu.Unlock()
	if cut.AtMs%1000 != 0 || cut.WindowMs <= cut.AtMs || last.why != "window" || last.at.UnixMilli() < cut.AtMs+1100 {
		t.Fatalf("cut %+v, last nudge %s at %d: AtMs on a whole second, and the window must pass it through a nudge after the hold", cut, last.why, last.at.UnixMilli())
	}
	db.mu.Lock()
	db.stopOnNudge = "window"
	db.mu.Unlock()
	write(t, r, "load-b", WriteConfig{})
	if _, err := TakeCut(context.Background(), r, "c2", []string{"load-a", "load-b"}, "", false, 2*time.Second); err == nil || !strings.Contains(err.Error(), "extend the restore window") {
		t.Fatalf("cut whose window never reaches it: %v", err)
	}
	if _, err := r.Cut("c2"); err == nil {
		t.Fatal("a cut no restore can reach was recorded")
	}
}

func TestSlowExactCutEnforcesQuiescence(t *testing.T) {
	t.Parallel()
	t.Run("a written phase outside applied", func(t *testing.T) {
		r, _ := newTestRun(t, testDocs, testSplit)
		write(t, r, "load-a", WriteConfig{})
		write(t, r, "load-b", WriteConfig{MaxBatches: 1})
		if _, err := TakeCut(context.Background(), r, "c", []string{"load-a"}, "", true, time.Second); err == nil || !strings.Contains(err.Error(), "load-b has") {
			t.Fatalf("cut with load-b written but not applied: %v", err)
		}
	})
	t.Run("a writer runs while the cut is taken", func(t *testing.T) {
		r, _ := newTestRun(t, testDocs, testSplit)
		write(t, r, "load-a", WriteConfig{})
		go func() {
			time.Sleep(300 * time.Millisecond)
			_ = appendLine(filepath.Join(r.Dir, "ledger", "load-b.jsonl"), Event{Phase: "load-b", Ev: EvStart, Ms: time.Now().UnixMilli()})
		}()
		if _, err := TakeCut(context.Background(), r, "c", []string{"load-a"}, "", false, 10*time.Second); err == nil || !strings.Contains(err.Error(), "while the cut was taken") {
			t.Fatalf("cut while a ledger grew: %v", err)
		}
	})
}

func TestSlowRejectionAfterAnAmbiguousAttemptIsUnresolved(t *testing.T) {
	t.Parallel()
	check := func(t *testing.T, r *Run, db *fakeDB, err error) {
		t.Helper()
		if err == nil || !strings.Contains(err.Error(), "after an ambiguous attempt") {
			t.Fatalf("a 400 after an attempt that applied: %v, want unresolved", err)
		}
		states, _ := ReadLedger(r, "load-a")
		if st := states[0]; st == nil || st.Terminal != "" || !st.Flagged {
			t.Fatalf("batch 0 ledger state %+v: want unresolved, no terminal event", st)
		}
		db.setFault(nil)
		if stats := write(t, r, "load-a", WriteConfig{Writers: 1}); stats.Present != 1 {
			t.Fatalf("resume %+v: want batch 0 proven by read", stats)
		}
		checkExactlyOnce(t, r, db, "load-a")
		checkPhase(t, r, db, "load-a")
	}
	t.Run("same invocation", func(t *testing.T) {
		r, db := newTestRun(t, testDocs, testSplit)
		db.setFault(func(n int) *fault {
			switch n {
			case 1:
				return &fault{abortAfter: true} // applied, acknowledgement lost
			case 2:
				return &fault{reject: 400}
			}
			return nil
		})
		_, err := Write(context.Background(), r, WriteConfig{Phase: "load-a", Writers: 1, Timeout: time.Second})
		check(t, r, db, err)
	})
	t.Run("an earlier invocation", func(t *testing.T) {
		r, db := newTestRun(t, testDocs, testSplit)
		crashed := false
		cfg := WriteConfig{Phase: "load-a", Writers: 1, Timeout: time.Second, hook: func(at string, b int64) error {
			if at == "after-response" && !crashed {
				crashed = true
				return errCrash // the first attempt applied; this process died before recording it
			}
			return nil
		}}
		if _, err := Write(context.Background(), r, cfg); !errors.Is(err, errCrash) {
			t.Fatalf("crash run: %v", err)
		}
		db.setFault(func(n int) *fault { return &fault{reject: 400} })
		_, err := Write(context.Background(), r, WriteConfig{Phase: "load-a", Writers: 1, Timeout: time.Second})
		check(t, r, db, err)
	})
}

func TestSlowExactCutWaitsForTheBackupToListTheDatabase(t *testing.T) {
	t.Parallel()
	r, db := newTestRun(t, testDocs, testSplit)
	write(t, r, "load-a", WriteConfig{})
	setListed := func(listed bool) {
		db.mu.Lock()
		db.noBackup = !listed
		db.mu.Unlock()
	}
	// Discovery runs every 10 s: a new database is not listed at first.
	setListed(false)
	time.AfterFunc(700*time.Millisecond, func() { setListed(true) })
	if _, err := TakeCut(context.Background(), r, "c1", []string{"load-a"}, "", false, 10*time.Second); err != nil {
		t.Fatalf("cut while the database was not listed yet: %v", err)
	}
	setListed(false)
	if _, err := TakeCut(context.Background(), r, "c2", []string{"load-a"}, "", false, time.Second); err == nil || !strings.Contains(err.Error(), "does not list") {
		t.Fatalf("cut on a database never listed: %v", err)
	}
}
