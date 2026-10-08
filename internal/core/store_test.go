package core

import (
	"regexp"
	"strings"
	"testing"
	"time"
)

func TestNewULID(t *testing.T) {
	before := time.Now().UnixMilli()
	first, second := NewULID(), NewULID()
	after := time.Now().UnixMilli()
	if first == second {
		t.Fatal("two generated ULIDs are identical")
	}
	for _, id := range []string{first, second} {
		if !regexp.MustCompile(`^[0-9A-HJKMNP-TV-Z]{26}$`).MatchString(id) || id[0] > '7' {
			t.Fatalf("generated ID %q is not a ULID", id)
		}
		var ms int64
		for _, c := range id[:10] {
			ms = ms<<5 | int64(strings.IndexRune("0123456789ABCDEFGHJKMNPQRSTVWXYZ", c))
		}
		if ms < before || ms > after {
			t.Fatalf("ULID timestamp %d is outside [%d, %d]", ms, before, after)
		}
	}
}
