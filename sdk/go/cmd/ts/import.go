package main

import (
	"context"
	"errors"
	"fmt"
	"io"
	"os"
	"strconv"
	"strings"

	"terrascale.tech/sdk/v1"
)

const (
	maxImportDumpBytes          = 32 << 20
	maxImportStatementsPerBatch = 256
	maxImportBatchBytes         = 900 * 1024
	maxImportStatementBytes     = 16 * 1024
)

func dbImport(ctx context.Context, db *v1.Database, path string, from int, input io.Reader, out, errOut io.Writer) error {
	if path != "-" {
		file, err := os.Open(path)
		if err != nil {
			return err
		}
		defer file.Close()
		input = file
	}
	data, err := io.ReadAll(io.LimitReader(input, maxImportDumpBytes+1))
	if err != nil {
		return err
	}
	if len(data) > maxImportDumpBytes {
		return fmt.Errorf("SQL dump exceeds the %d MiB import limit", maxImportDumpBytes>>20)
	}
	script := string(data)
	total, wrapped, err := validateImport(script)
	if err != nil {
		return err
	}
	from = max(from, 1)
	if from > total {
		if total == 0 {
			return nil
		}
		return fmt.Errorf("-from %d skips past the %d statements in %s: %w", from, total, path, errUsage)
	}
	done := from - 1
	sent, err := runImportBatches(ctx, db, script, wrapped, from, total, func(n int) {
		fmt.Fprintf(errOut, "imported %d/%d statements\n", done+n, total)
	})
	if err != nil {
		if sent > 0 && !errors.Is(err, v1.ErrCommitUncertain) {
			return fmt.Errorf("%w\nresume with: ts db import %s %s -from %d", err, db.Name(), path, from+sent)
		}
		return err
	}
	fmt.Fprintf(out, "imported %d/%d statements into %s\n", total, total, db.Name())
	return nil
}

func validateImport(script string) (total int, wrapped bool, err error) {
	var last string
	var tooLarge error
	for statement, err := range scanSQLStatements(script) {
		if err != nil {
			return 0, false, err
		}
		if total == 0 && !wrapped && strings.EqualFold(statement, "BEGIN") {
			wrapped = true
			continue
		}
		total++
		last = statement
		if tooLarge == nil && len(statement) > maxImportStatementBytes {
			tooLarge = fmt.Errorf("statement %d is %d bytes, over the %d-byte limit", total, len(statement), maxImportStatementBytes)
		}
	}
	if wrapped {
		if !strings.EqualFold(last, "COMMIT") {
			return 0, false, errors.New("dump starts with BEGIN but does not end with COMMIT")
		}
		total--
	}
	return total, wrapped, tooLarge
}

func runImportBatches(ctx context.Context, db *v1.Database, script string, wrapped bool, from, total int, progress func(int)) (int, error) {
	sent, ordinal, size := 0, 0, 0
	if wrapped {
		ordinal = -1
	}
	batch := make([]string, 0, maxImportStatementsPerBatch)
	done := func(n int) {
		sent += n
		progress(sent)
	}
	for statement, err := range scanSQLStatements(script) {
		if err != nil {
			return sent, err
		}
		ordinal++
		if ordinal < from {
			continue
		}
		if ordinal > total {
			break
		}
		if len(batch) == maxImportStatementsPerBatch || size+len(statement) > maxImportBatchBytes {
			if _, err := sendImportBatch(ctx, db, batch, from+sent, done); err != nil {
				return sent, err
			}
			batch, size = batch[:0], 0
		}
		batch = append(batch, statement)
		size += len(statement)
	}
	if len(batch) > 0 {
		if _, err := sendImportBatch(ctx, db, batch, from+sent, done); err != nil {
			return sent, err
		}
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
