package ui

import (
	"html"
	"strings"
	"testing"
)

func TestRegionMapRendersInitialSelection(t *testing.T) {
	var out strings.Builder
	if err := RegionMap("region", "GRU", "FRA").Render(t.Context(), &out); err != nil {
		t.Fatal(err)
	}
	markup := html.UnescapeString(out.String())
	for _, tc := range []struct {
		code, pressed string
	}{
		{"SFO", "false"},
		{"GRU", "true"},
		{"FRA", "true"},
	} {
		button := `data-region="` + tc.code + `"`
		start := strings.Index(markup, button)
		if start < 0 || !strings.Contains(markup[start:strings.Index(markup[start:], ">")+start], `aria-pressed="`+tc.pressed+`"`) {
			t.Errorf("%s pin should start with aria-pressed=%s", tc.code, tc.pressed)
		}
	}
	if !strings.Contains(markup, `data-selected="GRU FRA"`) {
		t.Fatal("map does not show the initial selection before JavaScript runs")
	}
}

func TestRegionMapQuotesSelectIDInEveryExpression(t *testing.T) {
	id := "region'\\\n\"<"
	want := `"region'\\\n\"\u003c"`
	if got := regionJSString(id); got != want {
		t.Fatalf("quoted ID = %q, want %q", got, want)
	}
	if !strings.Contains(regionSync(id), "document.getElementById("+want+")") || !strings.Contains(regionPick(id), "document.getElementById("+want+")") {
		t.Fatal("region handler does not quote the select ID")
	}
	var out strings.Builder
	if err := RegionMap(id).Render(t.Context(), &out); err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(html.UnescapeString(out.String()), "document.getElementById("+want+").contains(evt.target)") {
		t.Fatal("change handler does not quote the select ID")
	}
}
