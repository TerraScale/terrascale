package main

import (
	"context"
	"io"
	"strings"

	v1 "terrascale.tech/sdk/v1"
)

func validRegion(s string) bool {
	return len(s) > 0 && len(s) <= 63 && s[0] >= 'a' && s[0] <= 'z' && s[len(s)-1] != '-' && !strings.ContainsFunc(s, func(r rune) bool {
		return !(r >= 'a' && r <= 'z' || r >= '0' && r <= '9' || r == '-')
	})
}

// createDatabaseIn creates a database served from region; the server checks
// that the region serves.
func createDatabaseIn(ctx context.Context, db *v1.Database, region string, out io.Writer) error {
	result, err := db.CreateIn(ctx, region)
	if err != nil {
		return err
	}
	return emitJSON(out, result)
}
