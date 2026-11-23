package main

import (
	"context"
	"fmt"
	"io"
	"strings"

	v1 "terrascale.tech/sdk/v1"
)

func runSQL(ctx context.Context, db *v1.Database, statement string, input io.Reader, out, diagnostics io.Writer) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	if statement == "-" {
		if input == nil {
			return fmt.Errorf("SQL stdin is unavailable: %w", errUsage)
		}
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
	if err := writeSQLResult(out, res, db.Bookmark()); err != nil {
		return err
	}
	if jsonMode(out) || db.Bookmark() == "" {
		return nil
	}
	_, err = fmt.Fprintf(diagnostics, "bookmark %s\n", db.Bookmark())
	return err
}

func executeSQL(ctx context.Context, db *v1.Database, statement string) (*v1.Result, error) {
	if strings.TrimSpace(statement) == "" || len(statement) > v1.MaxSQLRequestBytes {
		return nil, fmt.Errorf("SQL input must be nonempty and within the server statement limit: %w", errUsage)
	}
	return db.Execute(ctx, statement)
}

type sqlOutput struct {
	*v1.Result
	Bookmark string `json:"bookmark,omitempty"`
}

func writeSQLResult(out io.Writer, res *v1.Result, bookmark string) error {
	if jsonMode(out) {
		return emitJSON(out, sqlOutput{res, bookmark})
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

func causalOptions(ctx context.Context, name, consistency, bookmark string) (context.Context, error) {
	if name != "sql" && name != "shell" {
		return ctx, fmt.Errorf("-consistency and -bookmark need sql or shell: %w", errUsage)
	}
	if consistency == "" && bookmark == "" {
		return ctx, fmt.Errorf("-consistency and -bookmark need a value: %w", errUsage)
	}
	if consistency != "" {
		mode, err := v1.ParseConsistency(consistency)
		if err != nil {
			return ctx, fmt.Errorf("-consistency is fast or strong: %w", errUsage)
		}
		ctx = v1.WithConsistency(ctx, mode)
	}
	if bookmark != "" {
		if _, err := v1.MergeBookmarks(bookmark); err != nil {
			return ctx, fmt.Errorf("-bookmark must be a TerraScale-Bookmark value printed by an earlier command: %w", errUsage)
		}
		ctx = v1.WithBookmark(ctx, bookmark)
	}
	return ctx, nil
}
