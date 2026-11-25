package main

import (
	"bufio"
	"bytes"
	"context"
	"errors"
	"fmt"
	"io"
	"unicode"
)

type sqlStatementReader struct {
	input    *bufio.Reader
	maxBytes int
	text     []byte
}

func (r *sqlStatementReader) next(ctx context.Context) (string, error) {
	r.text = r.text[:0]
	var boundary sqlStatementBoundary
	lastTokenEnd := 0
	for {
		ch, err := r.readByte(ctx)
		if errors.Is(err, io.EOF) && lastTokenEnd > 0 {
			return string(r.text[:lastTokenEnd]), nil
		}
		if err != nil {
			return "", err
		}
		switch {
		case unicode.IsSpace(rune(ch)):
			if lastTokenEnd > 0 {
				r.appendByte(ch)
			}
			continue
		case ch == '-' && r.peek() == '-', ch == '/' && r.peek() == '*':
			if err := r.readComment(ctx, ch, lastTokenEnd > 0); err != nil {
				return "", err
			}
			continue
		case ch == ';' && boundary.depth == 0:
			if lastTokenEnd == 0 {
				continue
			}
			if err := r.sizeError(); err != nil {
				return "", err
			}
			return string(r.text), nil
		}
		start := len(r.text)
		r.appendByte(ch)
		if err := r.sizeError(); err != nil {
			return "", err
		}
		if quote := sqlQuoteEnd(ch); quote != 0 {
			if err := r.readQuoted(ctx, quote); err != nil {
				return "", err
			}
		} else if sqlWordStart(ch) {
			for sqlWordPart(r.peek()) {
				if _, err := r.readTokenByte(ctx); err != nil {
					return "", err
				}
			}
		}
		lastTokenEnd = len(r.text)
		boundary.observe(r.text[start:lastTokenEnd])
	}
}

func (r *sqlStatementReader) readByte(ctx context.Context) (byte, error) {
	if err := ctx.Err(); err != nil {
		return 0, err
	}
	return r.input.ReadByte()
}

func (r *sqlStatementReader) peek() byte {
	if b, _ := r.input.Peek(1); len(b) != 0 {
		return b[0]
	}
	return 0
}

func (r *sqlStatementReader) appendByte(b byte) {
	if r.maxBytes == 0 || len(r.text) <= r.maxBytes {
		r.text = append(r.text, b)
	}
}

func (r *sqlStatementReader) sizeError() error {
	if r.maxBytes > 0 && len(r.text) > r.maxBytes {
		return fmt.Errorf("SQL statement exceeds the %d-byte limit", r.maxBytes)
	}
	return nil
}

func (r *sqlStatementReader) readTokenByte(ctx context.Context) (byte, error) {
	ch, err := r.readByte(ctx)
	if err != nil {
		return 0, err
	}
	r.appendByte(ch)
	return ch, r.sizeError()
}

func (r *sqlStatementReader) readQuoted(ctx context.Context, quote byte) error {
	for {
		ch, err := r.readTokenByte(ctx)
		if errors.Is(err, io.EOF) {
			return errors.New("unterminated SQL quote")
		}
		if err != nil {
			return err
		}
		if ch != quote {
			continue
		}
		if quote == ']' || r.peek() != quote {
			return nil
		}
		if _, err := r.readTokenByte(ctx); err != nil {
			return err
		}
	}
}

func (r *sqlStatementReader) readComment(ctx context.Context, first byte, keep bool) error {
	second, err := r.readByte(ctx)
	if err != nil {
		return err
	}
	if keep {
		r.appendByte(first)
		r.appendByte(second)
	}
	var previous byte
	for {
		ch, err := r.readByte(ctx)
		if errors.Is(err, io.EOF) {
			if first == '-' {
				return nil
			}
			return errors.New("unterminated SQL comment")
		}
		if err != nil {
			return err
		}
		if keep {
			r.appendByte(ch)
		}
		if first == '-' && ch == '\n' || first == '/' && previous == '*' && ch == '/' {
			return nil
		}
		previous = ch
	}
}

func sqlQuoteEnd(b byte) byte {
	switch b {
	case '\'', '"', '`':
		return b
	case '[':
		return ']'
	}
	return 0
}

func sqlWordStart(b byte) bool {
	return b == '_' || b >= 128 || b >= 'a' && b <= 'z' || b >= 'A' && b <= 'Z' || b >= '0' && b <= '9'
}

func sqlWordPart(b byte) bool { return b == '$' || sqlWordStart(b) }

type sqlStatementPrefix uint8

const (
	sqlStatementStart sqlStatementPrefix = iota
	sqlAfterCreate
	sqlAfterCreateTemp
	sqlTrigger
	sqlOrdinaryStatement
)

type sqlStatementBoundary struct {
	prefix sqlStatementPrefix
	depth  int
}

func (b *sqlStatementBoundary) observe(token []byte) {
	switch b.prefix {
	case sqlStatementStart:
		b.prefix = sqlOrdinaryStatement
		if bytes.EqualFold(token, []byte("CREATE")) {
			b.prefix = sqlAfterCreate
		}
	case sqlAfterCreate:
		switch {
		case bytes.EqualFold(token, []byte("TRIGGER")):
			b.prefix = sqlTrigger
		case bytes.EqualFold(token, []byte("TEMP")), bytes.EqualFold(token, []byte("TEMPORARY")):
			b.prefix = sqlAfterCreateTemp
		default:
			b.prefix = sqlOrdinaryStatement
		}
	case sqlAfterCreateTemp:
		b.prefix = sqlOrdinaryStatement
		if bytes.EqualFold(token, []byte("TRIGGER")) {
			b.prefix = sqlTrigger
		}
	case sqlTrigger:
		switch {
		case bytes.EqualFold(token, []byte("BEGIN")), bytes.EqualFold(token, []byte("CASE")):
			b.depth++
		case bytes.EqualFold(token, []byte("END")):
			b.depth--
		}
	}
}
