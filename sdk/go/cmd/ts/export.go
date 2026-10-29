package main

import (
	"context"
	"fmt"
	"io"

	v1 "terrascale.tech/sdk/v1"
)

// exportDatabase preserves raw import-compatible NDJSON by default. JSON mode
// wraps each record and signals completion only after clean HTTP EOF. The server
// reads one document snapshot; relational tables and metadata are excluded.
func exportDatabase(ctx context.Context, db *v1.Database, out io.Writer) error {
	beginJSONStream(out)
	for document, err := range db.Export(ctx, v1.ExportOptions{}) {
		if err != nil {
			return fmt.Errorf("export incomplete; discard partial output before retrying: %w", err)
		}
		if err := emitJSONRecord(out, document); err != nil {
			return fmt.Errorf("write export: %w", err)
		}
	}
	if err := ctx.Err(); err != nil {
		return err
	}
	return finishJSONStream(out)
}
