package charts

import (
	"bytes"
	"fmt"
	"strings"
	"testing"
	"time"
)

func TestManySeriesKeepDistinctStrokesAcrossChartKeys(t *testing.T) {
	series := make([]Series, 12)
	for i := range series {
		series[i] = Series{Name: fmt.Sprint("series ", i), Values: []int64{int64(i)}, Missing: []bool{false}}
	}
	data := NewLine("many series", nil, nil, 200, series...)
	seen := make(map[string]bool)
	for i, path := range data.Paths {
		style := path.Class + "/" + path.Dash
		if seen[style] {
			t.Fatalf("series %d repeats visual style %q", i, style)
		}
		seen[style] = true
		row, dot, marker := data.Columns[0].Tip.Rows[i], data.Columns[0].Dots[i], data.Markers[i]
		if row.Class != path.Class || dot.Class != path.Class || marker.Class != path.Class || row.Dash != path.Dash || dot.Dash != path.Dash || marker.Dash != path.Dash {
			t.Fatalf("series %d keys do not match its curve", i)
		}
	}
	var html bytes.Buffer
	if err := Line(data).Render(t.Context(), &html); err != nil {
		t.Fatal(err)
	}
	if err := Legend(data).Render(t.Context(), &html); err != nil {
		t.Fatal(err)
	}
	// Each dashed series renders its pattern on the curve, marker, hover dot,
	// tooltip key and legend stroke; the first three keep their solid strokes.
	if got := strings.Count(html.String(), `stroke-dasharray="6 5"`); got != 15 {
		t.Fatalf("first repeated color group has %d patterned elements, want 15", got)
	}
}

func TestIsolatedObservationsRenderPersistentMarkers(t *testing.T) {
	denseValues, denseMissing := make([]int64, 121), make([]bool, 121)
	for i := range denseMissing {
		denseMissing[i] = true
	}
	denseValues[60], denseMissing[60] = 4, false
	for _, tc := range []struct {
		name    string
		values  []int64
		missing []bool
		markers int
	}{
		{"intermediate", []int64{0, 4, 0}, []bool{true, false, true}, 1},
		{"measured zero at start", []int64{0, 99, 4}, []bool{false, true, false}, 2},
		{"one bucket", []int64{4}, nil, 1},
		{"connected observations", []int64{2, 4, 0}, []bool{false, false, true}, 0},
		{"all missing", []int64{2, 4, 0}, []bool{true, true, true}, 0},
		{"outside sampled hover columns", denseValues, denseMissing, 1},
	} {
		t.Run(tc.name, func(t *testing.T) {
			data := NewLine("nullable", nil, nil, 200, Series{Name: "p95", Values: tc.values, Missing: tc.missing})
			var html bytes.Buffer
			if err := Line(data).Render(t.Context(), &html); err != nil {
				t.Fatal(err)
			}
			if got := strings.Count(html.String(), `class="ts-chart-dot ts-chart-marker `); got != tc.markers {
				t.Fatalf("persistent markers=%d, want %d", got, tc.markers)
			}
		})
	}
}

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
