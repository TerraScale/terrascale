package main

import (
	"context"
	"errors"
	"fmt"
	"io"
	"os"
	"strconv"

	"terrascale.tech/sdk/v1"
)

const (
	maxImportStatementsPerBatch = 256
	maxImportBatchBytes         = 900 * 1024
	maxImportStatementBytes     = 16 * 1024
)

func dbImport(ctx context.Context, db *v1.Database, path string, from int, input io.Reader, out, errOut io.Writer) error {
	var script []byte
	var err error
	if path == "-" {
		script, err = io.ReadAll(input)
	} else {
		script, err = os.ReadFile(path)
	}
	if err != nil {
		return err
	}
	statements := splitSQL(string(script))
	total := len(statements)
	if from < 1 {
		from = 1
	}
	if from > total {
		if total == 0 {
			return nil
		}
		return fmt.Errorf("-from %d skips past the %d statements in %s: %w", from, total, path, errUsage)
	}
	for i, st := range statements {
		if len(st) > maxImportStatementBytes {
			return fmt.Errorf("statement %d is %d bytes, over the %d-byte limit", i+1, len(st), maxImportStatementBytes)
		}
	}
	done := from - 1
	sent, err := runImportBatches(ctx, db, statements[from-1:], func(n int) {
		fmt.Fprintf(errOut, "imported %d/%d statements\n", done+n, total)
	})
	var sqlErr *v1.SQLError
	if errors.As(err, &sqlErr) {
		return fmt.Errorf("%w\nresume with: ts db import %s %s -from %d", err, db.Name(), path, from+sent)
	}
	if err != nil {
		return err
	}
	fmt.Fprintf(out, "imported %d/%d statements into %s\n", total, total, db.Name())
	return nil
}

func runImportBatches(ctx context.Context, db *v1.Database, statements []string, progress func(int)) (int, error) {
	sent := 0
	for len(statements) > 0 {
		count := min(len(statements), maxImportStatementsPerBatch)
		size := 0
		for i := 0; i < count; i++ {
			if size+len(statements[i]) > maxImportBatchBytes && i > 0 {
				count = i
				break
			}
			size += len(statements[i])
		}
		done := func(n int) {
			sent += n
			progress(sent)
		}
		if _, err := sendImportBatch(ctx, db, statements[:count], sent+1, done); err != nil {
			return sent, err
		}
		statements = statements[count:]
	}
	return sent, nil
}

func sendImportBatch(ctx context.Context, db *v1.Database, batch []string, first int, done func(int)) (int, error) {
	items := make([]v1.Statement, len(batch))
	for i, st := range batch {
		items[i] = v1.Statement{SQL: st}
	}
	_, err := db.Batch(ctx, items)
	var sqlErr *v1.SQLError
	if errors.As(err, &sqlErr) && sqlErr.Code == v1.ErrTooLarge && len(batch) > 1 {
		half := len(batch) / 2
		committed, err := sendImportBatch(ctx, db, batch[:half], first, done)
		if err != nil {
			return committed, err
		}
		rest, err := sendImportBatch(ctx, db, batch[half:], first+half, done)
		return committed + rest, err
	}
	if err != nil {
		return 0, fmt.Errorf("statement %d: %w", first, err)
	}
	done(len(batch))
	return len(batch), nil
}

func parseImportFrom(value string) (int, error) {
	if value == "" {
		return 1, nil
	}
	n, err := strconv.Atoi(value)
	if err != nil || n < 1 {
		return 0, fmt.Errorf("-from needs a statement number of 1 or more: %w", errUsage)
	}
	return n, nil
}
