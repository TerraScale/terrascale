package core

import (
	"errors"
	"testing"
)

func TestParsePlacementRegion(t *testing.T) {
	for _, tc := range []struct{ input, want string }{
		{"GRU", "GRU"}, {" sfo ", "SFO"}, {"gru", "GRU"},
		{"FRA", ""}, {"fra", ""}, {"IAD", ""}, {"", ""},
	} {
		t.Run(tc.input, func(t *testing.T) {
			region, err := ParsePlacementRegion(tc.input)
			if string(region) != tc.want || tc.want == "" && !errors.Is(err, ErrInvalid) || tc.want != "" && err != nil {
				t.Fatalf("ParsePlacementRegion(%q) = %q, %v; want %q", tc.input, region, err, tc.want)
			}
		})
	}
}
