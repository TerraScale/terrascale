package main

import (
	"bufio"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"iter"
	"os"
	"strconv"
	"strings"

	"terrascale.tech/sdk/v1"
)

const (
	maxImportStatementBytes = 16 * 1024
	importBatchEnvelopeSize = len(`{"statements":[]}`)
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
	from = max(from, 1)
	sent, total, err := runImportBatches(ctx, db, input, from, func(n int) {
		fmt.Fprintf(errOut, "imported %d statements\n", from-1+n)
	})
	if err != nil {
		if sent > 0 && !errors.Is(err, v1.ErrCommitUncertain) {
			return fmt.Errorf("%w\nresume with: ts db import %s %s -from %d", err, db.Name(), path, from+sent)
		}
		return err
	}
	if total == 0 {
		return nil
	}
	if from > total {
		return fmt.Errorf("-from %d skips past the %d statements in %s: %w", from, total, path, errUsage)
	}
	fmt.Fprintf(out, "imported %d/%d statements into %s\n", total, total, db.Name())
	return nil
}

func importStatements(ctx context.Context, input io.Reader) iter.Seq2[string, error] {
	return func(yield func(string, error) bool) {
		reader := sqlStatementReader{input: bufio.NewReader(input), maxBytes: maxImportStatementBytes}
		statement, err := reader.next(ctx)
		if err == nil && strings.EqualFold(strings.TrimSpace(statement), "PRAGMA foreign_keys=OFF") {
			pragma := statement
			statement, err = reader.next(ctx)
			if (err != nil || !importTransaction(statement, "BEGIN")) && !yield(pragma, nil) {
				return
			}
		}
		wrapped := err == nil && importTransaction(statement, "BEGIN")
		if wrapped {
			statement, err = reader.next(ctx)
		}
		for err == nil {
			if wrapped && importTransaction(statement, "COMMIT") {
				_, err = reader.next(ctx)
				if err == nil {
					yield("", errors.New("dump COMMIT must be the final statement"))
				} else if !errors.Is(err, io.EOF) {
					yield("", err)
				}
				return
			}
			if !yield(statement, nil) {
				return
			}
			statement, err = reader.next(ctx)
		}
		if !errors.Is(err, io.EOF) {
			yield("", err)
		} else if wrapped {
			yield("", errors.New("dump starts with BEGIN but does not end with COMMIT"))
		}
	}
}

func importTransaction(statement, verb string) bool {
	statement = strings.TrimSpace(statement)
	if strings.EqualFold(statement, verb) {
		return true
	}
	return len(statement) > len(verb) && strings.EqualFold(statement[:len(verb)], verb) &&
		strings.ContainsRune(" \t\r\n\v\f", rune(statement[len(verb)])) &&
		strings.EqualFold(strings.TrimSpace(statement[len(verb):]), "TRANSACTION")
}

func runImportBatches(ctx context.Context, db *v1.Database, input io.Reader, from int, progress func(int)) (int, int, error) {
	sent, total, size := 0, 0, importBatchEnvelopeSize
	batch := make([]v1.Statement, 0, v1.MaxSQLStatements)
	done := func(n int) {
		sent += n
		progress(sent)
	}
	flush := func() error {
		_, err := sendImportBatch(ctx, db, batch, from+sent, done)
		clear(batch)
		batch, size = batch[:0], importBatchEnvelopeSize
		return err
	}
	for statement, err := range importStatements(ctx, input) {
		if err != nil {
			return sent, total, fmt.Errorf("statement %d: %w", total+1, err)
		}
		total++
		if total < from {
			continue
		}
		item := v1.Statement{SQL: statement}
		encoded, err := json.Marshal(item)
		if err != nil {
			return sent, total, fmt.Errorf("statement %d: %w", total, err)
		}
		if size+len(encoded)+1 > v1.MaxSQLRequestBytes {
			if err := flush(); err != nil {
				return sent, total, err
			}
		}
		batch = append(batch, item)
		size += len(encoded) + 1
		if len(batch) == v1.MaxSQLStatements {
			if err := flush(); err != nil {
				return sent, total, err
			}
		}
	}
	if len(batch) > 0 {
		err := flush()
		return sent, total, err
	}
	return sent, total, nil
}

func sendImportBatch(ctx context.Context, db *v1.Database, batch []v1.Statement, first int, done func(int)) (int, error) {
	_, err := db.Batch(ctx, batch)
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
