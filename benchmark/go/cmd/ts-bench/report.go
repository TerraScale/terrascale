package main

import (
	"fmt"
	"slices"
	"strings"
	"time"
)

func renderMarkdown(mode string, cfg config, reports []sdkReport) string {
	var b strings.Builder
	fmt.Fprintf(&b, "# TerraScale E2E Benchmark Results\n\n")
	fmt.Fprintf(&b, "- Date: %s\n", time.Now().UTC().Format(time.RFC3339))
	fmt.Fprintf(&b, "- Mode: %s\n", mode)
	fmt.Fprintf(&b, "- Iterations: %d (warmup: %d, batch size: %d)\n", cfg.iterations, cfg.warmup, cfg.batchSize)
	fmt.Fprintf(&b, "- Runners: %s\n\n", strings.Join(cfg.runners, ", "))

	var scenarioOrder []string
	bySDK := make(map[string]map[string]scenarioResult, len(reports))
	sdkNames := make([]string, 0, len(reports))
	for _, rep := range reports {
		sdkNames = append(sdkNames, rep.SDK)
		m := make(map[string]scenarioResult, len(rep.Scenarios))
		for _, s := range rep.Scenarios {
			m[s.Name] = s
			if !slices.Contains(scenarioOrder, s.Name) {
				scenarioOrder = append(scenarioOrder, s.Name)
			}
		}
		bySDK[rep.SDK] = m
	}

	b.WriteString("| Scenario | SDK | Ops/s | p50 (ms) | p95 (ms) | p99 (ms) | Mean (ms) |\n")
	b.WriteString("| --- | --- | ---: | ---: | ---: | ---: | ---: |\n")
	for _, scenario := range scenarioOrder {
		for _, sdk := range sdkNames {
			res, ok := bySDK[sdk][scenario]
			if !ok {
				continue
			}
			fmt.Fprintf(&b, "| `%s` | %s | %.1f | %.2f | %.2f | %.2f | %.2f |\n",
				scenario, sdk, res.OpsPerSec, res.P50Ms, res.P95Ms, res.P99Ms, res.MeanMs)
		}
	}
	return b.String()
}
