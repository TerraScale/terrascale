//go:build slow

package pitrbench

import (
	"testing"
	"time"
)

// Retain the complete critical PITR lifecycle, including its real backup-window waits, in Slow.
func TestSlowPitrLifecycleWritesCutsAndVerifies(t *testing.T) {
	t.Parallel()
	r, db := newTestRun(t, fenceDocs, fenceSplit)
	phases := []string{"load-a", "load-b", "u1", "d1", "r1"}
	for _, phase := range phases {
		write(t, r, phase, WriteConfig{})
		checkPhase(t, r, db, phase)
	}
	checkExactlyOnce(t, r, db, phases...)
	checkOracle(t, r, db, phases...)
	cut, err := TakeCut(t.Context(), r, "fast", phases, "", false, 10*time.Second)
	if err != nil || cut.Kind != "exact" || cut.WindowMs <= cut.AtMs {
		t.Fatalf("exact cut %+v: %v", cut, err)
	}
	wantPassed(t, verify(t, r, VerifyConfig{Cut: "fast", Versions: "exact"}))
}
