package replbench

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

func TestReportVerdictAndSections(t *testing.T) {
	res := Result{
		Name: "t", Kind: "local", Start: time.Date(2026, 10, 12, 0, 0, 0, 0, time.UTC), Regions: []string{"gru", "sjc"},
		Writes:      map[string]RegionWrites{"gru": {Created: 10}, "sjc": {Created: 10}},
		Lag:         map[string]Lag{"gru->sjc": {Summary: Summary{Count: 3, P50: 1}}},
		Convergence: Convergence{Converged: true, Expected: 20, Regions: map[string]RegionCheck{"gru": {Count: 20}, "sjc": {Count: 20}}},
		Conflicts:   ConflictOutcome{Docs: 2, Identical: 2, SameField: map[string]int{"gru": 2}},
		Resources:   Resources{Source: "test", Nodes: map[string]NodeResources{"gru": {DiskMB: 1}}},
		NotTested:   []string{"reads"},
	}
	if !res.Passed() {
		t.Fatal("a converged run with identical conflicts passes")
	}
	md := res.Markdown()
	for _, want := range []string{"**PASSED**", "## Writes", "## Replication lag", "## Convergence and correctness", "## Conflicts", "## Resources", "## Not tested", "| gru->sjc | 3 |"} {
		if !strings.Contains(md, want) {
			t.Fatalf("report lacks %q:\n%s", want, md)
		}
	}
	for _, broken := range []func(r *Result){
		func(r *Result) { r.Convergence.Converged = false },
		func(r *Result) { r.Convergence.Unknown = 1 },
		func(r *Result) { r.Conflicts.Identical = 1 },
		func(r *Result) { r.Conflicts.SameField = map[string]int{"invalid": 1} },
		func(r *Result) { r.Errors = []string{"load: boom"} },
	} {
		bad := res
		bad.Conflicts.SameField = map[string]int{"gru": 2}
		broken(&bad)
		if bad.Passed() || !strings.Contains(bad.Markdown(), "**FAILED**") {
			t.Fatalf("want a failed verdict for %+v", bad)
		}
	}
	dir := t.TempDir()
	path, err := res.Write(filepath.Join(dir, "out"))
	if err != nil || filepath.Base(path) != "2026-10-12-t.md" {
		t.Fatalf("write: %s %v", path, err)
	}
	if _, err := os.Stat(strings.TrimSuffix(path, ".md") + ".json"); err != nil {
		t.Fatal(err)
	}
}
