package main

import (
	"bufio"
	"errors"
	"fmt"
	"io"
	"os"
	"strings"

	"golang.org/x/term"
)

func stdinTerminal() bool { return term.IsTerminal(int(os.Stdin.Fd())) }

func confirmDelete(database, project string, yes bool, in io.Reader, out io.Writer, tty bool) error {
	if yes {
		return nil
	}
	if !tty {
		return errors.New("database deletion requires a terminal: pass -yes before db delete to explicitly consent")
	}
	if project == "" {
		project = "automatic selection (the only accessible project)"
	}
	if _, err := fmt.Fprintf(out, "Permanently delete database %q and its data. Project selector: %s.\nType the database name to confirm: ", database, project); err != nil {
		return err
	}
	line, err := bufio.NewReader(in).ReadString('\n')
	if err != nil {
		return fmt.Errorf("database deletion cancelled: %w", err)
	}
	if strings.TrimSuffix(strings.TrimSuffix(line, "\n"), "\r") != database {
		return errors.New("database deletion cancelled: name did not match")
	}
	return nil
}
