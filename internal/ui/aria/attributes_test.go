package aria

import "testing"

func TestNavigationAttributesAreScalarValues(t *testing.T) {
	if got := CurrentPage(true); got != "page" {
		t.Fatalf("active aria-current = %q", got)
	}
	if got := CurrentPage(false); got != "false" {
		t.Fatalf("inactive aria-current = %q", got)
	}
	if got := Selected(true); got != "true" {
		t.Fatalf("active aria-selected = %q", got)
	}
	if got := Selected(false); got != "false" {
		t.Fatalf("inactive aria-selected = %q", got)
	}
}
