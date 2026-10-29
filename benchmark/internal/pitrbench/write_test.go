package pitrbench

import (
	"context"
	"testing"
	"time"
)

const (
	testDocs  = 4000
	testSplit = 2000
)

func write(t *testing.T, r *Run, phase string, cfg WriteConfig) WriteStats {
	t.Helper()
	cfg.Phase = phase
	if cfg.Writers == 0 {
		cfg.Writers = 2
	}
	if cfg.Timeout == 0 {
		cfg.Timeout = 2 * time.Second
	}
	stats, err := Write(context.Background(), r, cfg)
	if err != nil {
		t.Fatalf("%s: %v", phase, err)
	}
	return stats
}

func checkPhase(t *testing.T, r *Run, db *fakeDB, phase string) {
	t.Helper()
	p, _ := r.Phase(phase)
	if err := phaseComplete(r, p); err != nil {
		t.Fatalf("%s: %v", phase, err)
	}
	v, err := OpenVersions(r, phase)
	if err != nil {
		t.Fatal(err)
	}
	defer v.Close()
	members := p.Members(r.Docs)
	db.mu.Lock()
	defer db.mu.Unlock()
	for slot, i := range members {
		got, _ := v.Get(int64(slot))
		d := db.docs[DocID(r.Seed, i)]
		if d == nil || got != d.version {
			t.Fatalf("%s slot %d (document %d): recorded version %q, database has %+v", phase, slot, i, got, d)
		}
	}
}

func checkExactlyOnce(t *testing.T, r *Run, db *fakeDB, phases ...string) {
	t.Helper()
	db.mu.Lock()
	defer db.mu.Unlock()
	for i := range r.Docs {
		want := 0
		for _, name := range phases {
			if p, _ := r.Phase(name); p.Contains(i) {
				want++
			}
		}
		if got := db.applied[DocID(r.Seed, i)]; got != want {
			t.Fatalf("document %d applied %d writes, want %d", i, got, want)
		}
	}
}
