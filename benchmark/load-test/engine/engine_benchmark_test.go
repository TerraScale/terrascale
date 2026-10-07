package engine

import (
	"context"
	"fmt"
	"testing"
	"time"
)

// BenchmarkSchedule measures this client's scheduler only. It makes no network
// calls and cannot qualify TerraBase performance. Keep a bench/ Markdown table
// whenever using these numbers to compare changes.
func BenchmarkSchedule(b *testing.B) {
	for _, rate := range []int{100, 1000, 5000, 10000} {
		b.Run(fmt.Sprintf("rate_%d", rate), func(b *testing.B) {
			var report Report
			for i := 0; i < b.N; i++ {
				var err error
				report, err = Run(context.Background(), Config{
					Rate: rate, Concurrency: 64, Duration: time.Second,
					RequestTimeout: time.Second, DrainTimeout: time.Second,
					Execute: func(context.Context, int, bool) error { return nil },
				})
				if err != nil {
					b.Fatal(err)
				}
			}
			b.ReportMetric(report.SuccessfulOpsPerSecond, "successful_ops/s")
			b.ReportMetric(float64(report.Skipped), "skipped/phase")
			b.ReportMetric(float64(report.Dropped), "dropped/phase")
			b.ReportMetric(float64(report.Errors), "errors/phase")
			b.ReportMetric(float64(report.SuccessfulReads), "reads/phase")
			b.ReportMetric(float64(report.SuccessfulWrites), "writes/phase")
			b.ReportMetric(float64(report.EndUserLatency.All.P50)/float64(time.Millisecond), "arrival_p50_ms")
			b.ReportMetric(float64(report.EndUserLatency.All.P99)/float64(time.Millisecond), "arrival_p99_ms")
		})
	}
}
