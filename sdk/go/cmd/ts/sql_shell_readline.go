package main

import (
	"bufio"
	"context"
	"errors"
	"io"
	"os"
	"sync"

	"github.com/chzyer/readline"
	"golang.org/x/term"
	v1 "terrascale.tech/sdk/v1"
)

type batchSQLLines struct{ scanner *bufio.Scanner }

func (*batchSQLLines) SetPrompt(string)         {}
func (*batchSQLLines) SaveHistory(string) error { return nil }
func (b *batchSQLLines) Readline() (string, error) {
	if b.scanner.Scan() {
		return b.scanner.Text(), nil
	}
	if err := b.scanner.Err(); err != nil {
		return "", err
	}
	return "", io.EOF
}

type terminalSQLLines struct{ *readline.Instance }

func (t terminalSQLLines) Readline() (string, error) {
	line, err := t.Instance.Readline()
	if errors.Is(err, readline.ErrInterrupt) {
		return "", context.Canceled
	}
	return line, err
}

func sqlShell(ctx context.Context, db *v1.Database, input commandInput, out io.Writer) error {
	file, terminal := input.in.(*os.File)
	if !input.tty || !terminal {
		scanner := bufio.NewScanner(input.in)
		scanner.Buffer(make([]byte, 4096), sqlInputLimit("document")+1)
		stop := context.AfterFunc(ctx, func() {
			if closer, ok := input.in.(io.Closer); ok {
				closer.Close()
			}
		})
		defer stop()
		return runSQLShell(ctx, db, &batchSQLLines{scanner}, out, input.prompt)
	}
	var state *term.State
	rl, err := readline.NewEx(&readline.Config{
		Stdin: readline.NewCancelableStdin(input.in), Stdout: input.prompt, Stderr: input.prompt,
		HistoryLimit: 500,
		// Third-party options keep partial lines out of history and bind raw mode to the supplied terminal.
		DisableAutoSaveHistory: true, FuncIsTerminal: func() bool { return true },
		FuncMakeRaw: func() (err error) { state, err = term.MakeRaw(int(file.Fd())); return err },
		FuncExitRaw: func() error {
			if state == nil {
				return nil
			}
			return term.Restore(int(file.Fd()), state)
		},
		InterruptPrompt: "\n", EOFPrompt: "\n",
	})
	if err != nil {
		return err
	}
	var once sync.Once
	closeEditor := func() { once.Do(func() { rl.Close() }) }
	defer closeEditor()
	stop := context.AfterFunc(ctx, closeEditor)
	defer stop()
	return runSQLShell(ctx, db, terminalSQLLines{rl}, out, input.prompt)
}
