package core

import (
	"regexp"
	"strings"
	"testing"
	"time"
)

func TestNewULID(t *testing.T) {
	before := time.Now().UnixMilli()
	first, second := NewID(), NewID()
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

func TestNewIDSameMillisecondMonotonic(t *testing.T) {
	const ms = 1720000000000
	previous := newIDAt(ms)
	for range 1000 {
		next := newIDAt(ms)
		if !ValidID(next) || next <= previous || next[:10] != previous[:10] {
			t.Fatalf("same-millisecond IDs are not increasing: %q, %q", previous, next)
		}
		previous = next
	}
}
