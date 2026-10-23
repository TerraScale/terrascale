package main

import (
	"testing"

	"terrascale.tech/ts-benchmark/internal/replbench"
)

func TestPartitionOutcomeRejectsFalseSuccessAndRetryAmbiguity(t *testing.T) {
	t.Parallel()
	for _, tc := range []struct {
		name, body   string
		status       int
		retry, valid bool
	}{
		{"unknown", `{"error":{"code":"write_outcome_unknown"}}`, 503, false, true},
		{"success-status", `{"error":{"code":"write_outcome_unknown"}}`, 201, false, false},
		{"unsafe-retry", `{"error":{"code":"write_outcome_unknown"}}`, 503, true, false},
		{"false-durability", `{"error":{"code":"write_outcome_unknown"},"durability":"remote_durable"}`, 503, false, false},
		{"wrong-code", `{"error":{"code":"remote_region_unavailable"}}`, 503, false, false},
	} {
		t.Run(tc.name, func(t *testing.T) {
			t.Parallel()
			err := requireOutcome(replbench.Response{Status: tc.status, Body: []byte(tc.body), HasRetryAfter: tc.retry}, 503, "write_outcome_unknown", false)
			if (err == nil) != tc.valid {
				t.Fatalf("valid=%t: %v", tc.valid, err)
			}
		})
	}
}
