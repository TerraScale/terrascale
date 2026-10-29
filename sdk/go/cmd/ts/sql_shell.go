package main

import (
	"context"
	"errors"
	"fmt"
	"io"
	"strings"

	v1 "terrascale.tech/sdk/v1"
)

const sqlShellHelp = `SQL shell: .run submits the entire buffer; .reset discards it.
.mode relational uses SQL tables; each .run is one transaction.
.mode document uses the read-only collection SELECT subset (limited results).
.quit exits; .help shows this help. Enter SQL across any number of lines.
History is session-only; use Up/Down to recall submitted statements.
`

type sqlLineReader interface {
	Readline() (string, error)
	SetPrompt(string)
	SaveHistory(string) error
}

// runSQLShell uses explicit submission instead of guessing SQL boundaries.
// In particular it never splits strings, comments or trigger bodies at ';'.
func runSQLShell(ctx context.Context, db *v1.Database, lines sqlLineReader, out, diagnostics io.Writer) error {
	beginJSONStream(out)
	if _, err := io.WriteString(diagnostics, sqlShellHelp); err != nil {
		return err
	}
	mode := "relational"
	var pending strings.Builder
	var failure error
	finish := func() error {
		if pending.Len() != 0 {
			return errors.Join(failure, fmt.Errorf("SQL buffer not submitted; use .run or .reset: %w", io.ErrUnexpectedEOF))
		}
		if failure != nil {
			return failure
		}
		return finishJSONStream(out)
	}
	for {
		if err := ctx.Err(); err != nil {
			return err
		}
		prompt := db.Name() + "[" + mode + "]> "
		if pending.Len() != 0 {
			prompt = "... .run> "
		}
		lines.SetPrompt(prompt)
		line, err := lines.Readline()
		if ctx.Err() != nil {
			return ctx.Err()
		}
		if errors.Is(err, io.EOF) {
			return finish()
		}
		if err != nil {
			return err
		}
		trimmed := strings.TrimSpace(line)
		switch {
		case trimmed == ".quit":
			return finish()
		case trimmed == ".help":
			if _, err := io.WriteString(diagnostics, sqlShellHelp); err != nil {
				return err
			}
		case trimmed == ".reset":
			pending.Reset()
		case trimmed == ".mode" || strings.HasPrefix(trimmed, ".mode "):
			next := strings.TrimSpace(strings.TrimPrefix(trimmed, ".mode "))
			if pending.Len() != 0 || (next != "relational" && next != "document") {
				if failure == nil {
					failure = fmt.Errorf("invalid .mode command or pending SQL buffer: %w", errUsage)
				}
				if _, err := fmt.Fprintln(diagnostics, "ts: reset the buffer first; .mode requires relational or document"); err != nil {
					return err
				}
				continue
			}
			mode = next
		case trimmed == ".run":
			statement := pending.String()
			pending.Reset()
			if strings.TrimSpace(statement) == "" {
				continue
			}
			if err := lines.SaveHistory(statement); err != nil {
				return fmt.Errorf("save SQL session history: %w", err)
			}
			result, err := executeSQL(ctx, db, mode, statement)
			if err != nil {
				if ctx.Err() != nil {
					return ctx.Err()
				}
				if failure == nil {
					failure = err
				}
				if _, writeErr := fmt.Fprintln(diagnostics, "ts:", err); writeErr != nil {
					return writeErr
				}
				continue
			}
			if jsonMode(out) {
				err = emitJSONRecord(out, result)
			} else {
				err = writeSQLResult(out, result, diagnostics)
			}
			if err != nil {
				return err
			}
		default:
			if strings.HasPrefix(trimmed, ".") {
				return fmt.Errorf("unknown SQL shell command; use .help: %w", errUsage)
			}
			if trimmed == "" && pending.Len() == 0 {
				continue
			}
			if pending.Len()+len(line)+1 > sqlInputLimit(mode) {
				return fmt.Errorf("SQL buffer exceeds the server statement limit: %w", errUsage)
			}
			pending.WriteString(line)
			pending.WriteByte('\n')
		}
	}
}
