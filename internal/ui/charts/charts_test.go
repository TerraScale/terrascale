package charts

import (
	"context"
	"strconv"
	"strings"
	"testing"
)

func TestLineGeometryKeepsZeroBaselineAndDistinctSeries(t *testing.T) {
	zero := NewLine("Zero", []string{"Start", "", "Middle", "", "End"}, nil, 180, Series{Name: "Operations", Values: make([]int64, 7)})
	if len(zero.Paths) != 1 || zero.Paths[0].Class != "ts-chart-series-1" || zero.AreaPath == "" || !strings.Contains(zero.Paths[0].Data, ",148.0") {
		t.Fatalf("flat zero chart geometry = %#v", zero)
	}
	multiple := NewLine("Sample", nil, nil, 200,
		Series{Name: "Reads", Values: []int64{1, 4, 2}},
		Series{Name: "Writes", Values: []int64{2, 1, 3}},
		Series{Name: "Replication", Values: []int64{1, 1, 1}},
	)
	if len(multiple.Paths) != 3 || multiple.Paths[2].Class != "ts-cat-3" || multiple.AreaPath != "" {
		t.Fatalf("three-series chart geometry = %#v", multiple)
	}
	if invalid := NewLine("Invalid", nil, nil, 200, Series{Values: []int64{1}}, Series{Values: []int64{1, 2}}); len(invalid.Paths) != 0 {
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
	if got := NewBar("None", -10, "nothing"); got.Width != "0" {
		t.Fatalf("negative progress = %#v", got)
	}
	if got := NewBar("Over", 163, "the plan"); got.Width != "100" || got.Value != "163%" || got.Label != "Over: 163 percent of the plan" {
		t.Fatalf("overage progress = %#v", got)
	}
}

func TestLineHoverColumnsListEverySeriesValue(t *testing.T) {
	chart := NewLine("Hover", nil, []string{"2h ago", "1h ago", "Now"}, 200,
		Series{Name: "Reads", Values: []int64{1, 4, 2}},
		Series{Name: "Writes", Values: []int64{2, 1, 1_234_567}},
	)
	if len(chart.Columns) != 3 || chart.Columns[2].Label != "Now: Reads 2, Writes 1,234,567" || len(chart.Columns[2].Dots) != 2 {
		t.Fatalf("hover columns = %#v", chart.Columns)
	}
	tip := chart.Columns[2].Tip
	if tip.Header != "Now" || len(tip.Rows) != 2 || tip.Rows[1] != (ChartTipRow{Name: "Writes", Value: "1,234,567", Class: "ts-cat-2", Y: tip.Rows[1].Y}) {
		t.Fatalf("tooltip rows = %#v", tip)
	}
	if !strings.Contains(chart.Paths[0].Data, " C ") || chart.GradientID == "" {
		t.Fatalf("line is not a smooth curve with a named fill: %#v", chart.Paths[0])
	}
}

func TestDenseLineSharesHoverColumns(t *testing.T) {
	values := make([]int64, 168)
	chart := NewLine("Dense", nil, nil, 200, Series{Name: "Reads", Values: values})
	if len(chart.Columns) > maxColumns || len(chart.Columns) < maxColumns/2 {
		t.Fatalf("dense chart rendered %d hover columns", len(chart.Columns))
	}
}

func TestLineTooltipStaysInsideThePlot(t *testing.T) {
	chart := NewLine("Placement", nil, nil, 200, Series{Name: "Replication", Values: []int64{10, 20, 30}})
	number := func(value string) float64 {
		parsed, err := strconv.ParseFloat(value, 64)
		if err != nil {
			t.Fatal(err)
		}
		return parsed
	}
	first, last := chart.Columns[0], chart.Columns[2]
	if number(first.Tip.X) <= number(first.X) {
		t.Fatalf("first tooltip at %s should sit right of its guide at %s", first.Tip.X, first.X)
	}
	if right := number(last.Tip.X) + number(last.Tip.Width); right >= number(last.X) || right > float64(chart.PlotEnd) {
		t.Fatalf("last tooltip ends at %.1f, want it flipped left of the guide at %s", right, last.X)
	}
	if first.Tip.Header != "" || first.Tip.HeaderY != "" || number(first.Tip.Height) != 2*tipPadY+tipLine {
		t.Fatalf("header-less tooltip = %#v", first.Tip)
	}
}

func TestLineRendersFocusableTooltips(t *testing.T) {
	chart := NewLine("Rendered", nil, []string{"1h ago", "Now"}, 200, Series{Name: "Operations", Values: []int64{1200, 5}})
	var out strings.Builder
	if err := Line(chart).Render(context.Background(), &out); err != nil {
		t.Fatal(err)
	}
	html := out.String()
	for _, want := range []string{
		`role="group" aria-label="Rendered"`,
		`<g class="ts-chart-column" tabindex="0" role="img" aria-label="1h ago: Operations 1,200">`,
		`<g class="ts-chart-tip" aria-hidden="true">`,
		`<circle class="ts-chart-tip-swatch ts-chart-series-1"`,
		`>1h ago</text>`, `>Operations</text>`, `>1,200</text>`,
	} {
		if !strings.Contains(html, want) {
			t.Fatalf("line chart is missing %q: %s", want, html)
		}
	}
	if strings.Contains(html, "<title>") {
		t.Fatal("native <title> tooltips would double the styled tooltip")
	}
}

func TestMetersRenderTooltips(t *testing.T) {
	var out strings.Builder
	if err := Bar(NewBar("orders", 42, "stored bytes")).Render(context.Background(), &out); err != nil {
		t.Fatal(err)
	}
	if err := Ring(NewRing("Sample writes", 8_140_200, 5_000_000)).Render(context.Background(), &out); err != nil {
		t.Fatal(err)
	}
	html := out.String()
	for _, want := range []string{
		`<span class="ts-chart-meter" tabindex="0" role="img" aria-label="orders: 42 percent of stored bytes">`,
		`<span class="ts-chart-tooltip-label">orders</span> <strong>42%</strong>`,
		`<span class="ts-chart-tooltip-label">Sample writes</span> <strong>163%</strong>`,
	} {
		if !strings.Contains(html, want) {
			t.Fatalf("meter is missing %q: %s", want, html)
		}
	}
}

func TestGroupedWritesExactValues(t *testing.T) {
	for value, want := range map[int64]string{0: "0", 999: "999", 1000: "1,000", -45_000: "-45,000", 8_140_200: "8,140,200"} {
		if got := grouped(value); got != want {
			t.Fatalf("grouped(%d) = %q, want %q", value, got, want)
		}
	}
}
