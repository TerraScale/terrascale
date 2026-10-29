package pitrbench

import (
	"encoding/json"
	"os"
	"testing"
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

func readJSON(path string, v any) error {
	b, err := os.ReadFile(path)
	if err != nil {
		return err
	}
	return json.Unmarshal(b, v)
}
