package pitrbench

import (
	"context"
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

const (
	fenceDocs  = 14
	fenceSplit = 13
)

func holdNext(db *fakeDB, k int) ([]chan struct{}, chan int) {
	db.mu.Lock()
	base := db.tx
	db.mu.Unlock()
	holds := make([]chan struct{}, k)
	for j := range holds {
		holds[j] = make(chan struct{})
	}
	evaluated := make(chan int, k)
	db.setFault(func(n int) *fault {
		if j := n - base - 1; j >= 0 && j < k {
			return &fault{hold: holds[j], evaluated: evaluated}
		}
		return nil
	})
	return holds, evaluated
}

func checkOracle(t *testing.T, r *Run, db *fakeDB, phases ...string) {
	t.Helper()
	o, _ := NewOracle(r, phases)
	db.mu.Lock()
	defer db.mu.Unlock()
	for i := range r.Docs {
		e, d := o.At(i), db.docs[DocID(r.Seed, i)]
		live := d != nil && d.live
		if e.Alive != live || (e.Alive && string(d.body) != string(Body(r.Seed, i, e.Rev))) {
			t.Fatalf("document %d: database %+v, oracle after %v %+v", i, d, phases, e)
		}
	}
}

func TestFenceRejectsALoadAttemptHeldPastTheDelete(t *testing.T) {
	t.Parallel()
	for _, control := range []bool{false, true} {
		name := "fenced"
		if control {
			name = "control: assertions ignored"
		}
		t.Run(name, func(t *testing.T) {
			r, db := newTestRun(t, fenceDocs, fenceSplit)
			db.ignoreAsserts = control
			write(t, r, "load-a", WriteConfig{})
			holds, evaluated := holdNext(db, 1)
			write(t, r, "load-b", WriteConfig{Writers: 1, Timeout: 500 * time.Millisecond})
			db.setFault(nil)
			write(t, r, "u1", WriteConfig{})
			write(t, r, "d1", WriteConfig{})
			close(holds[0])
			status := <-evaluated
			id := DocID(r.Seed, 13)
			db.mu.Lock()
			resurrected := db.docs[id].live
			db.mu.Unlock()
			if control {
				if status != 200 || !resurrected {
					t.Fatalf("control: the late create answered %d, resurrected %v: without the fence it must re-create 13", status, resurrected)
				}
				return
			}
			if status != 412 || resurrected {
				t.Fatalf("the late create answered %d, resurrected %v: the fence must reject it", status, resurrected)
			}
			checkExactlyOnce(t, r, db, "load-a", "load-b", "u1", "d1")
			checkOracle(t, r, db, "load-a", "load-b", "u1", "d1")
		})
	}
}

func TestFenceRejectsAnUpdateAttemptHeldPastDeleteAndRecreate(t *testing.T) {
	t.Parallel()
	r, db := newTestRun(t, fenceDocs, fenceSplit)
	write(t, r, "load-a", WriteConfig{})
	write(t, r, "load-b", WriteConfig{})
	holds, evaluated := holdNext(db, 2)
	write(t, r, "u1", WriteConfig{Writers: 1, Timeout: 500 * time.Millisecond})
	db.setFault(nil)
	write(t, r, "d1", WriteConfig{})
	close(holds[0])
	if status := <-evaluated; status != 412 {
		t.Fatalf("a u1 attempt released after d1 answered %d, want 412", status)
	}
	write(t, r, "r1", WriteConfig{})
	close(holds[1])
	if status := <-evaluated; status != 412 {
		t.Fatalf("a u1 attempt released after r1 answered %d, want 412", status)
	}
	checkExactlyOnce(t, r, db, "load-a", "load-b", "u1", "d1", "r1")
	checkPhase(t, r, db, "r1")
	checkOracle(t, r, db, "load-a", "load-b", "u1", "d1", "r1")
}

func TestFenceRejectsLoadAttemptsHeldAcrossDeleteAndRecreate(t *testing.T) {
	t.Parallel()
	r, db := newTestRun(t, fenceDocs, fenceSplit)
	write(t, r, "load-a", WriteConfig{})
	holds, evaluated := holdNext(db, 2)
	write(t, r, "load-b", WriteConfig{Writers: 1, Timeout: 500 * time.Millisecond})
	db.setFault(nil)
	write(t, r, "u1", WriteConfig{})
	write(t, r, "d1", WriteConfig{})
	close(holds[0])
	if status := <-evaluated; status != 412 {
		t.Fatalf("a load-b attempt released between d1 and r1 answered %d, want 412", status)
	}
	write(t, r, "r1", WriteConfig{})
	versions, _ := OpenVersions(r, "r1")
	before, _ := versions.Get(0)
	close(holds[1])
	if status := <-evaluated; status != 412 {
		t.Fatalf("a load-b attempt released after r1 answered %d, want 412", status)
	}
	after, _ := versions.Get(0)
	versions.Close()
	if before == "" || before != after {
		t.Fatalf("r1's recorded version went from %q to %q", before, after)
	}
	checkExactlyOnce(t, r, db, "load-a", "load-b", "u1", "d1", "r1")
	checkPhase(t, r, db, "r1")
	checkOracle(t, r, db, "load-a", "load-b", "u1", "d1", "r1")
}

func TestAnUnresolvedBatchBlocksTheFenceTheNextPhaseAndCuts(t *testing.T) {
	t.Parallel()
	r, db := newTestRun(t, testDocs, testSplit)
	db.setFault(func(n int) *fault { return &fault{reject: 503} })
	if _, err := Write(context.Background(), r, WriteConfig{Phase: "load-a", Writers: 1, MaxAttempts: 1, Timeout: time.Second}); err == nil {
		t.Fatal("load-a with no proven outcome succeeded")
	}
	db.setFault(nil)
	c, _ := r.Client(time.Second)
	want := "unresolved batches [0]"
	if err := CloseFence(context.Background(), c, r, "load-a"); err == nil || !strings.Contains(err.Error(), want) {
		t.Fatalf("fence over an unresolved batch: %v, want %q", err, want)
	}
	if _, err := Write(context.Background(), r, WriteConfig{Phase: "load-b", Writers: 1}); err == nil || !strings.Contains(err.Error(), want) {
		t.Fatalf("load-b after an unresolved load-a batch: %v, want %q", err, want)
	}
	for _, unshipped := range []bool{false, true} {
		if _, err := TakeCut(context.Background(), r, "c", []string{"load-a"}, "", unshipped, time.Second); err == nil || !strings.Contains(err.Error(), want) {
			t.Fatalf("cut (unshipped %v) over an unresolved batch: %v, want %q", unshipped, err, want)
		}
	}
	if fenced, err := Fenced(context.Background(), c, r, "load-a"); err != nil || fenced {
		t.Fatalf("load-a fenced %v (%v)", fenced, err)
	}
}

func TestAFencedPhaseNeverRunsAgain(t *testing.T) {
	t.Parallel()
	r, _ := newTestRun(t, testDocs, testSplit)
	if stats := write(t, r, "load-a", WriteConfig{}); !stats.Fenced {
		t.Fatalf("a complete load-a was not fenced: %+v", stats)
	}
	if _, err := Write(context.Background(), r, WriteConfig{Phase: "load-a", Writers: 1}); err == nil || !strings.Contains(err.Error(), "fenced") {
		t.Fatalf("rerunning a fenced phase: %v", err)
	}

}

func TestALostFenceAcknowledgementIsRetriedAndProvenByRead(t *testing.T) {
	t.Parallel()
	r, db := newTestRun(t, testDocs, testSplit)

	db.setFault(func(n int) *fault { return map[bool]*fault{true: {abortAfter: true}}[n == 3] })
	if stats := write(t, r, "load-a", WriteConfig{Writers: 1}); !stats.Fenced {
		t.Fatalf("load-a was not fenced after a lost fence answer: %+v", stats)
	}
	var ev struct {
		Proven    string   `json:"proven"`
		Attempts  int      `json:"attempts"`
		Ambiguous []string `json:"ambiguous_attempts"`
	}
	if err := readJSON(filepath.Join(r.Dir, "evidence", "fence-load-a.json"), &ev); err != nil {
		t.Fatal(err)
	}
	if ev.Attempts != 2 || len(ev.Ambiguous) != 1 || !strings.HasPrefix(ev.Proven, "existed") {
		t.Fatalf("fence evidence %+v: want an ambiguous attempt, then a 412 proven by read", ev)
	}
	if fences := db.live("fences"); len(fences) != 1 || fences[0] != "closed-load-a" {
		t.Fatalf("fences %v", fences)
	}
}

func readJSON(path string, v any) error {
	b, err := os.ReadFile(path)
	if err != nil {
		return err
	}
	return json.Unmarshal(b, v)
}
