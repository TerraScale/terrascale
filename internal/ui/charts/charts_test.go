package charts

import (
	"strings"
	"testing"
)

func TestLineGeometryKeepsZeroBaselineAndDistinctSeries(t *testing.T) {
	zero := NewLine("Zero", []string{"Start", "", "Middle", "", "End"}, 180, Series{Name: "Operations", Values: make([]int64, 7)})
	if len(zero.Paths) != 1 || zero.Paths[0].Class != "ts-chart-series-1" || zero.AreaPath == "" || !strings.Contains(zero.Paths[0].Data, ",148.0") {
		t.Fatalf("flat zero chart geometry = %#v", zero)
	}
	multiple := NewLine("Sample", nil, 200,
		Series{Name: "Reads", Values: []int64{1, 4, 2}},
		Series{Name: "Writes", Values: []int64{2, 1, 3}},
		Series{Name: "Replication", Values: []int64{1, 1, 1}},
	)
	if len(multiple.Paths) != 3 || multiple.Paths[2].Class != "ts-cat-3" || multiple.AreaPath != "" {
		t.Fatalf("three-series chart geometry = %#v", multiple)
	}
	if invalid := NewLine("Invalid", nil, 200, Series{Values: []int64{1}}, Series{Values: []int64{1, 2}}); len(invalid.Paths) != 0 {
		t.Fatal("mismatched series lengths were plotted")
	}
}

func TestRingClampsOverageWithoutHidingPercent(t *testing.T) {
	ring := NewRing("Sample writes", 8_140_200, 5_000_000)
	if ring.Percent != 163 || ring.Dash != "100 100" || ring.Tone != "danger" || !strings.Contains(ring.Label, "illustrative demo capacity") {
		t.Fatalf("sample overage ring = %#v", ring)
	}
}

func TestBarClampsProgressForCSPCompatibleSVG(t *testing.T) {
	if got := NewBar("None", -10); got.Width != "0" {
		t.Fatalf("negative progress = %#v", got)
	}
	if got := NewBar("Over", 163); got.Width != "100" {
		t.Fatalf("overage progress = %#v", got)
	}
}

func TestLineHoverColumnsListEverySeriesValue(t *testing.T) {
	chart := NewLine("Hover", nil, 200,
		Series{Name: "Reads", Values: []int64{1, 4, 2}},
		Series{Name: "Writes", Values: []int64{2, 1, 1500}},
	)
	if len(chart.Columns) != 3 || chart.Columns[2].Title != "Reads: 2 · Writes: 1.5k" || len(chart.Columns[2].Dots) != 2 {
		t.Fatalf("hover columns = %#v", chart.Columns)
	}
	if !strings.Contains(chart.Paths[0].Data, " C ") || chart.GradientID == "" {
		t.Fatalf("line is not a smooth curve with a named fill: %#v", chart.Paths[0])
	}
}

func TestDenseLineSharesHoverColumns(t *testing.T) {
	values := make([]int64, 168)
	chart := NewLine("Dense", nil, 200, Series{Name: "Reads", Values: values})
	if len(chart.Columns) > maxColumns || len(chart.Columns) < maxColumns/2 {
		t.Fatalf("dense chart rendered %d hover columns", len(chart.Columns))
	}
}
