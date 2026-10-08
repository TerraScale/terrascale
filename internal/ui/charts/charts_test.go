package charts

import (
	"strings"
	"testing"
	"time"
)

func TestNewLineLabelsTicksWithTheirSamplesWhenGivenNoTicks(t *testing.T) {
	points := []string{"a", "b", "c", "d", "e", "f", "g", "h", "i"}
	data := NewLine("chart", nil, points, 200, Series{Name: "Reads", Values: make([]int64, len(points))})
	for i, want := range []string{"a", "c", "e", "g", "i"} {
		if data.Ticks[i].Label != want {
			t.Fatalf("tick %d = %q, want %q", i, data.Ticks[i].Label, want)
		}
	}
	if data := NewLine("chart", []string{"then", "", "", "", "now"}, points, 200, Series{Values: make([]int64, len(points))}); data.Ticks[0].Label != "then" || data.Ticks[1].Label != "" {
		t.Fatalf("given ticks = %+v, want them kept", data.Ticks)
	}
}
func TestTimeTicksKeepSparseUTCLabelsDistinct(t *testing.T) {
	start := time.Date(2026, 10, 8, 12, 0, 0, 0, time.UTC)
	for _, count := range []int{0, 1, 2, 3, 4, 5, 60} {
		starts := make([]time.Time, count)
		for i := range starts {
			starts[i] = start.Add(time.Duration(i) * time.Minute)
		}
		ticks := TimeTicks(starts, "1h")
		seen := map[string]bool{}
		for _, tick := range ticks {
			if tick == "" {
				continue
			}
			if seen[tick] {
				t.Fatalf("%d buckets repeat %q", count, tick)
			}
			seen[tick] = true
		}
		if count > 1 && ticks[4] != starts[count-1].Format("15:04") {
			t.Fatalf("last tick=%q", ticks[4])
		}
	}
}
func TestLineLeavesGapsForUnknownObservations(t *testing.T) {
	data := NewLine("nullable", nil, []string{"first", "missing", "last"}, 200, Series{Name: "p95", Values: []int64{0, 999, 4}, Missing: []bool{false, true, false}})
	if len(data.Paths) != 1 || strings.Count(data.Paths[0].Data, "M") != 2 || data.AreaPath != "" || len(data.Columns[1].Dots) != 0 || !strings.Contains(data.Columns[1].Label, "Not reported") {
		t.Fatalf("chart=%+v", data)
	}
	if !strings.Contains(data.Columns[0].Label, "p95 0") {
		t.Fatal("measured zero was lost")
	}
}
