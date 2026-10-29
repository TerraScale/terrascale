package main

import (
	"os"
	"path/filepath"
	"slices"
	"testing"
)

// A fault case's injected object is expected in its audit; any other problem still fails the verify.
func TestReadAuditSetsTheInjectedKeyApart(t *testing.T) {
	t.Parallel()
	path := filepath.Join(t.TempDir(), "audit.json")
	audit := `{"verdict":"problems","problems":[
		{"kind":"unparseable_key","key":"v1/o/p/d/wal/g/0000000400000000-stray","detail":"not a snapshot or segment key"},
		{"kind":"gap","key":"v1/o/p/d/wal/g/0000000000000100-1","detail":"starts at 100, previous ends at 90"}],
		"generations":[{"snapshots":[{"at_ms":1}],"segments":[{"at_ms":2}]}]}`
	if err := os.WriteFile(path, []byte(audit), 0o600); err != nil {
		t.Fatal(err)
	}
	a, err := readAudit(path, "v1/o/p/d/wal/g/0000000400000000-stray")
	if err != nil || len(a.Expected) != 1 || len(a.Problems) != 1 || a.Problems[0][:3] != "gap" || !slices.Equal(a.Times, []int64{1, 2}) {
		t.Fatalf("audit %+v: %v", a, err)
	}
	// Only the injected problem: nothing fails, and the verdict is not turned into a problem of its own.
	only := `{"verdict":"problems","problems":[{"kind":"unparseable_key","key":"k","detail":"d"}],"generations":[]}`
	if err := os.WriteFile(path, []byte(only), 0o600); err != nil {
		t.Fatal(err)
	}
	if a, err := readAudit(path, "k"); err != nil || len(a.Problems) != 0 || len(a.Expected) != 1 {
		t.Fatalf("audit %+v: %v", a, err)
	}
	if a, err := readAudit(path, ""); err != nil || len(a.Problems) != 1 {
		t.Fatalf("no injected key: %+v: %v", a, err)
	}
}
