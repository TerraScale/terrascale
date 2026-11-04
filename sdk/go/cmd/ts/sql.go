package main

import (
	"context"
	"fmt"
	"io"
	"strings"

	v1 "terrascale.tech/sdk/v1"
)

// runSQL submits one complete statement in its own transaction; it does not
// split SQL or maintain a transaction across requests.
func runSQL(ctx context.Context, db *v1.Database, statement string, input io.Reader, out io.Writer) error {
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
		data, err := io.ReadAll(io.LimitReader(input, int64(v1.MaxSQLRequestBytes)+1))
		stop()
		if ctx.Err() != nil {
			return ctx.Err()
		}
		if err != nil {
			return fmt.Errorf("read SQL stdin: %w", err)
		}
		statement = string(data)
	}
	res, err := executeSQL(ctx, db, statement)
	if err != nil {
		return err
	}
	return writeSQLResult(out, res)
}

func executeSQL(ctx context.Context, db *v1.Database, statement string) (*v1.Result, error) {
	if strings.TrimSpace(statement) == "" || len(statement) > v1.MaxSQLRequestBytes {
		return nil, fmt.Errorf("SQL input must be nonempty and within the server statement limit: %w", errUsage)
	}
	return db.Execute(ctx, statement)
}

func writeSQLResult(out io.Writer, res *v1.Result) error {
	if jsonMode(out) {
		return emitJSON(out, res)
	}
	if len(res.Columns) == 0 {
		_, err := fmt.Fprintf(out, "%d rows affected\n", res.RowsAffected)
		return err
	}
	for _, row := range res.Rows {
		if err := emitJSON(out, row.Map()); err != nil {
			return err
		}
	}
	return nil
}
