package main

import (
	"context"
	"os"
	"path/filepath"
	"testing"

	"terrascale.tech/ts-benchmark/internal/apibench"
)

// A shell stand-in for a client: ready, one result per step line, done on EOF.
func TestRunClientDrivesProtocol(t *testing.T) {
	t.Parallel()
	bin := filepath.Join(t.TempDir(), "fake-client")
	script := "#!/bin/sh\n" +
		"echo '{\"type\":\"ready\",\"client\":\"fake\",\"sdk\":\"fake\"}'\n" +
		"while read -r line; do echo '{\"type\":\"result\",\"scenario\":\"doc.get\",\"ok\":1,\"status\":{\"ok\":1}}'; done\n" +
		"echo '{\"type\":\"done\",\"cleanup\":true}'\n"
	if err := os.WriteFile(bin, []byte(script), 0o755); err != nil {
		t.Fatal(err)
	}
	spec, err := apibench.LoadSpec("../../workloads/api-bench.json")
	if err != nil {
		t.Fatal(err)
	}
	plan := []apibench.Sweep{{Scenario: "doc.get", Transport: "sdk", PayloadBytes: 256, Concurrency: []int{1}}}
	cr := runClient(context.Background(), "fake", bin, "spec.json", "credentials.json", spec, plan)
	if cr.Error != "" || cr.Ready == nil || cr.Done == nil || !cr.Done.Cleanup || len(cr.Results) != 1 {
		t.Fatalf("client run %+v", cr)
	}
}
