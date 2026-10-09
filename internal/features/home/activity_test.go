package home

import (
	"context"
	"fmt"
	"slices"
	"strings"
	"testing"
	"time"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/core/memstore"
)

type seriesMeter struct{ err error }

func (m seriesMeter) Usage(_ context.Context, _ string, scope core.UsageScope) (core.Usage, error) {
	return core.Usage{Series: []core.UsagePoint{{Start: scope.From, Reads: 4, Writes: 1}, {Start: scope.From, Reads: 2}}}, m.err
}

func TestOverviewKeepsMeteredCountsAndPlotsOperations(t *testing.T) {
	store := memstore.New()
	account, err := store.CreateAccount(t.Context(), core.User{Name: "Owner", Email: "activity@example.test", PasswordHash: "unused"})
	if err != nil {
		t.Fatal(err)
	}
	service := Service{deps: core.Deps{Store: store, Usage: seriesMeter{}}}
	summary, err := service.Overview(t.Context(), account.Principal(), "token", "24h")
	if err != nil {
		t.Fatal(err)
	}
	if got := summary.Activity; !got.Metered || got.TotalReads != 6 || got.TotalWrites != 1 || len(got.Reads) != 2 || len(got.Labels) != 2 {
		t.Fatalf("activity = %+v, want 6 reads and 1 write over 2 buckets", got)
	}
	if got := summary.Activity; got.observedAt.IsZero() || len(got.bucketStarts) != 2 || got.bucketStarts[0].IsZero() {
		t.Fatalf("measured activity has no bucket times: %+v", got)
	}
	chart := activityChart(summary.Activity)
	if len(chart.Paths) != 1 || chart.AreaPath == "" || chart.Paths[0].Name != "Operations" {
		t.Fatalf("chart = %+v, want one filled operations series", chart)
	}
	if len(chart.Columns) != 2 || chart.Columns[0].Tip.Rows[0].Value != "5" || chart.Columns[1].Tip.Rows[0].Value != "2" {
		t.Fatalf("chart columns = %+v, want measured operation counts 5 and 2", chart.Columns)
	}
	if chart.Grid[0].Label != "5" || chart.Grid[4].Label != "0" {
		t.Fatalf("chart grid = %+v, want a zero-based scale fitted to the measured counts", chart.Grid)
	}
	for _, token := range []string{"", "token"} {
		service.deps.Usage = seriesMeter{err: core.ErrNotMetered}
		summary, err := service.Overview(t.Context(), account.Principal(), token, "")
		if err != nil {
			t.Fatal(err)
		}
		if chart := activityChart(summary.Activity); len(chart.Paths) != 0 {
			t.Fatalf("unmetered activity plotted invented operations: %+v", chart.Paths)
		}
		if got := summary.Activity; got.Metered || got.Sample || len(got.Reads) != 0 || got.Note == "" {
			t.Fatalf("unmetered activity with token %q = %+v", token, got)
		}
	}
}

func TestMeasuredActivityTicksFollowPlottedBucketTimes(t *testing.T) {
	now := time.Date(2026, time.January, 2, 12, 30, 0, 0, time.UTC)
	for _, tt := range []struct {
		name  string
		count int
		step  time.Duration
		lag   time.Duration
		want  []string
	}{
		{"1h", 1, time.Hour, 30 * time.Minute, []string{"30m ago", "", "", "", ""}},
		{"24h", 24, time.Hour, 30 * time.Minute, []string{"23.5h ago", "17.5h", "11.5h", "6.5h", "30m ago"}},
		{"7d", 168, time.Hour, 30 * time.Minute, []string{"7d ago", "5.2d", "3.5d", "1.8d", "30m ago"}},
		{"30d", 30, 24 * time.Hour, 12*time.Hour + 30*time.Minute, []string{"29.5d ago", "22.5d", "14.5d", "7.5d", "12.5h ago"}},
		{"delayed", 5, 24 * time.Hour, 2 * 24 * time.Hour, []string{"6d ago", "5d", "4d", "3d", "2d ago"}},
		{"short", 2, time.Hour, 30 * time.Minute, []string{"1.5h ago", "", "", "", "30m ago"}},
		{"current", 5, time.Hour, 0, []string{"4h ago", "3h", "2h", "1h", "Now"}},
	} {
		t.Run(tt.name, func(t *testing.T) {
			data := activity{Range: tt.name, Metered: true, observedAt: now}
			for i := range tt.count {
				start := now.Add(-tt.lag - time.Duration(tt.count-1-i)*tt.step)
				data.bucketStarts = append(data.bucketStarts, start)
				data.Labels = append(data.Labels, start.Format("Jan 2, 15:04 UTC"))
				data.Reads = append(data.Reads, int64(i+1))
				data.Writes = append(data.Writes, 0)
			}
			chart := activityChart(data)
			if tt.name == "1h" && !strings.Contains(chart.Label, "Current UTC hour") {
				t.Fatalf("chart label = %q, want the provider's current hourly bucket", chart.Label)
			}
			var got []string
			for _, tick := range chart.Ticks {
				got = append(got, tick.Label)
			}
			if !slices.Equal(got, tt.want) {
				t.Fatalf("ticks = %q, want %q", got, tt.want)
			}
			for _, column := range chart.Columns {
				var operations int
				if _, err := fmt.Sscan(column.Tip.Rows[0].Value, &operations); err != nil {
					t.Fatal(err)
				}
				if column.Tip.Header != data.Labels[operations-1] {
					t.Fatalf("tooltip lost bucket timestamp: %+v", column.Tip)
				}
			}
		})
	}
}

func TestActivityTicksPreserveSampleAndFallbackTimestampLabels(t *testing.T) {
	sample := activity{Range: "7d", Sample: true, Reads: make([]int64, 13), Writes: make([]int64, 13)}
	want := []string{"7d ago", "5d", "3d", "1d", "Now"}
	for i, tick := range activityChart(sample).Ticks {
		if tick.Label != want[i] {
			t.Fatalf("sample tick %d = %q, want %q", i, tick.Label, want[i])
		}
	}
	now := time.Date(2026, time.January, 2, 12, 30, 0, 0, time.UTC)
	for _, starts := range [][]time.Time{nil, {now.Add(time.Hour)}} {
		data := activity{Metered: true, Reads: []int64{1}, Writes: []int64{0}, Labels: []string{"Jan 2, 13:30 UTC"}, bucketStarts: starts, observedAt: now}
		for _, tick := range activityChart(data).Ticks {
			if tick.Label != data.Labels[0] {
				t.Fatalf("fallback tick = %q, want actual timestamp %q", tick.Label, data.Labels[0])
			}
		}
	}
}
