//go:build slow

package main

import (
	"context"
	"encoding/json"
	"testing"
	"time"
)

// The full 108-cell matrix belongs to Slow; Fast covers every lane and cleanup separately.
func TestSlowParityRunnerMeasuresEveryRegionThenCleansUp(t *testing.T) {
	t.Parallel()
	s, f, database := newParityService(t)
	regions := []string{"fra", "gru", "sjc"}
	plan := parityPlan{Warmup: 100 * time.Millisecond, Measure: 300 * time.Millisecond, Drain: 10 * time.Second}
	err := parityOwn(context.Background(), json.NewEncoder(s.log), database, "fra", paritySetupDeadline, func(documents map[int]parityDocument) error {
		return parityRegionsRun(context.Background(), json.NewEncoder(s.log), parityTestToken("a.b.c"), s.project, s.name, regions, documents, f.transport, plan)
	})
	rows := s.log.rows(t)
	if err != nil || s.requestsBeforeID != 0 || len(rows) != 2+108 {
		t.Fatalf("err %v, %d requests before the owned row, %d rows", err, s.requestsBeforeID, len(rows))
	}
	seen := map[string]int{}
	for _, row := range rows[1 : len(rows)-1] {
		if row["type"] != "cell" || len(row["rejected"].([]any)) != 0 || row["latency_us"] == nil {
			t.Fatalf("cell %v", row)
		}
		seen[row["region"].(string)]++
	}
	if seen["fra"] != 36 || seen["gru"] != 36 || seen["sjc"] != 36 {
		t.Fatalf("cells per region %v", seen)
	}
	if last := rows[len(rows)-1]; last["deletion"] != "logical_accepted" || last["remote_erasure"] != "unverified" || last["create"] != "created" || len(s.deletes) != 1 || s.deletes[0] != s.name {
		t.Fatalf("cleanup %v, deletes %v", last, s.deletes)
	}
}
