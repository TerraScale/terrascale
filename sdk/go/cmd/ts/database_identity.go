package main

import (
	"context"
	"fmt"
	"io"

	v1 "terrascale.tech/sdk/v1"
)

// Conditional deletion never modifies placement or falls back to a name-only request.
func deleteExpectedDatabase(ctx context.Context, db *v1.Database, expectedID string, out io.Writer) error {
	if err := db.DeleteIfID(ctx, expectedID); err != nil {
		return err
	}
	if jsonMode(out) {
		return emitJSON(out, struct {
			Name    string `json:"name"`
			Deleted bool   `json:"deleted"`
		}{db.Name(), true})
	}
	_, err := fmt.Fprintf(out, "Deleted %s\n", db.Name())
	return err
}
