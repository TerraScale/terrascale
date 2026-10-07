package charts

import "testing"

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
