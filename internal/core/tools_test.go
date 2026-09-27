package core

import (
	"errors"
	"fmt"
	"testing"
)

func TestInvalidReason(t *testing.T) {
	rejected := Invalid("Choose a database in this project.")
	if !errors.Is(rejected, ErrInvalid) || rejected.Error() != "invalid input: Choose a database in this project." {
		t.Fatalf("Invalid = %q, want an ErrInvalid naming the reason", rejected)
	}
	for _, tc := range []struct {
		err    error
		reason string
		ok     bool
	}{
		{err: rejected, reason: "Choose a database in this project.", ok: true},
		{err: fmt.Errorf("attach: %w", rejected), reason: "Choose a database in this project.", ok: true},
		{err: ErrInvalid},
		{err: ErrConflict},
	} {
		if reason, ok := InvalidReason(tc.err); reason != tc.reason || ok != tc.ok {
			t.Errorf("InvalidReason(%q) = %q, %v; want %q, %v", tc.err, reason, ok, tc.reason, tc.ok)
		}
	}
}
