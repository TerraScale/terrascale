package main

import (
	"context"
	"fmt"
	"io"
	"os"
	"strings"

	v1 "terrascale.tech/sdk/v1"
)

func sqlInputLimit(mode string) int {
	if mode == "document" {
		// Document SQL permits 16K characters, up to four UTF-8 bytes each.
		return 4 * v1.MaxSQLStatementBytes
	}
	return v1.MaxSQLStatementBytes
}

// runSQL submits one complete statement; it does not split SQL or maintain a
// transaction across requests. Document mode uses the server's read-only subset.
func runSQL(ctx context.Context, db *v1.Database, mode, statement string, input io.Reader, out io.Writer) error {
	if mode != "document" && mode != "relational" {
		return fmt.Errorf("SQL mode must be document or relational: %w", errUsage)
	}
	if err := ctx.Err(); err != nil {
		return err
	}
	if statement == "-" {
		if input == nil {
			return fmt.Errorf("SQL stdin is unavailable: %w", errUsage)
		}
		// A cancelled stdin command must release a blocked pipe/terminal read.
		stop := context.AfterFunc(ctx, func() {
			if closer, ok := input.(io.Closer); ok {
				closer.Close()
			}
		})
		data, err := io.ReadAll(io.LimitReader(input, int64(sqlInputLimit(mode))+1))
		stop()
		if ctx.Err() != nil {
			return ctx.Err()
		}
		if err != nil {
			return fmt.Errorf("read SQL stdin: %w", err)
		}
		statement = string(data)
	}
	res, err := executeSQL(ctx, db, mode, statement)
	if err != nil {
		return err
	}
	return writeSQLResult(out, res, os.Stderr)
}

func executeSQL(ctx context.Context, db *v1.Database, mode, statement string) (any, error) {
	if strings.TrimSpace(statement) == "" || len(statement) > sqlInputLimit(mode) {
		return nil, fmt.Errorf("SQL input must be nonempty and within the server statement limit: %w", errUsage)
	}
	switch mode {
	case "document":
		return db.SQL(ctx, statement)
	case "relational":
		return db.ExecuteSQL(ctx, statement)
	default:
		return nil, fmt.Errorf("SQL mode must be document or relational: %w", errUsage)
	}
}

func writeSQLResult(out io.Writer, result any, diagnostics io.Writer) error {
	if jsonMode(out) {
		return emitJSON(out, result)
	}
	switch res := result.(type) {
	case *v1.SQLResult:
		for _, row := range res.Rows {
			if err := emitJSON(out, row.Value); err != nil {
				return err
			}
		}
		if res.Truncated {
			_, err := fmt.Fprintf(diagnostics, "ts: truncated: %d rows matched\n", res.Matched)
			return err
		}
		return nil
	case *v1.SQLExecResult:
		if len(res.Columns) == 0 {
			_, err := fmt.Fprintf(out, "%d rows affected\n", res.RowsAffected)
			return err
		}
		for _, row := range res.Maps() {
			if err := emitJSON(out, row); err != nil {
				return err
			}
		}
		return nil
	default:
		return fmt.Errorf("unexpected SQL result type %T", result)
	}
}
