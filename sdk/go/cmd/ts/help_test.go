package main

import "testing"

func TestClosestCommand(t *testing.T) {
	t.Parallel()
	for _, tc := range []struct {
		input, want string
	}{
		{"sqlx", "sql"},
		{"db expor", "db export"},
		{"db ur", "db url"},
		{"keys lst", "keys list"},
		{"backup lst create", "backup create"},
		{"zzzzzzzzzz", ""},
	} {
		if got := closestCommand(tc.input); got != tc.want {
			t.Errorf("closestCommand(%q) = %q, want %q", tc.input, got, tc.want)
		}
	}
}
