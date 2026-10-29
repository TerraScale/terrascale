package main

import (
	"context"
	"errors"
	"fmt"
	"io"

	v1 "terrascale.tech/sdk/v1"
)

// ChangeEvent intentionally omits SDK cursor/type from JSON. Expose them at the
// CLI boundary, including cursor-only events and uncheckpointed commit members.
type changeRecord struct {
	Type   string `json:"type"`
	Cursor string `json:"cursor,omitempty"`
	*v1.ChangeEvent
}

func changesOperandCounts() []int {
	counts := make([]int, 65)
	for i := range counts {
		counts[i] = i + 1 // database, then zero through64 collection filters
	}
	return counts
}

func tailChangesFrom(ctx context.Context, db *v1.Database, cursor string, collections []string, out io.Writer) error {
	if len(collections) > 64 {
		return fmt.Errorf("db changes accepts at most 64 collection filters: %w", errUsage)
	}
	if cursor == "empty" {
		cursor = "" // The server starts at retained history when no cursor is sent.
	}
	beginJSONStream(out)
	for event, err := range db.Changes(ctx, v1.ChangesOptions{Cursor: cursor, Collections: collections}) {
		if errors.Is(err, context.Canceled) && !jsonMode(out) {
			return nil
		}
		if err != nil {
			return err // In particular, never rewind an expired resume cursor.
		}
		record := changeRecord{Type: event.Type, Cursor: event.Cursor}
		if event.Type == "change" {
			record.ChangeEvent = &event
		}
		if err := emitJSONRecord(out, record); err != nil {
			return err // Do not consume the next event after an output failure.
		}
	}
	if jsonMode(out) && ctx.Err() != nil {
		return ctx.Err()
	}
	return finishJSONStream(out)
}
