package main

import (
	"bufio"
	"errors"
	"fmt"
	"io"
	"os"
	"strings"

	"golang.org/x/term"
	v1 "terrascale.tech/sdk/v1"
)

func stdinTerminal() bool { return term.IsTerminal(int(os.Stdin.Fd())) }

func confirmDelete(database, project string, yes bool, in io.Reader, out io.Writer, tty bool) error {
	if yes {
		return nil
	}
	if !tty {
		return consentRequired("database deletion requires a terminal: pass -yes to explicitly consent")
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

func confirmResourceDelete(kind, database, target, project string, yes bool, in io.Reader, out io.Writer, tty bool) error {
	var command, consequence string
	switch kind {
	case "collection":
		command, consequence = "collection drop", "all its documents"
	case "function":
		command, consequence = "functions delete", "its invocation logs"
	default:
		return errUsage
	}
	if project == "" {
		project = "automatic selection (the only accessible project)"
	}
	message := fmt.Sprintf("Permanently delete %s %q and %s from database %q. Project selector: %s.", kind, target, consequence, database, project)
	return confirmNamedOperation(target, message, command, yes, in, out, tty)
}

func confirmImport(job *v1.ImportJob, yes bool, in io.Reader, out io.Writer, tty bool) error {
	if job == nil || job.Preview == nil || job.State != "ready" {
		return errors.New("import confirmation requires a ready preview")
	}
	if job.Preview.Blocking != 0 {
		return errors.New("cannot confirm an import with blocking issues")
	}
	message := fmt.Sprintf("Confirm import preview %s into database %q. Project: %s. Source: %s (%d bytes). Warnings: %d.",
		job.Preview.ID, job.Database, job.Project, job.Source.Format, job.Source.Bytes, job.Preview.Warnings)
	return confirmNamedOperation(job.Database, message, "import", yes, in, out, tty)
}

func confirmNamedOperation(target, message, command string, yes bool, in io.Reader, out io.Writer, tty bool) error {
	if yes {
		return nil
	}
	if !tty {
		return consentRequired("confirmation requires a terminal: pass -yes to explicitly consent to " + command)
	}
	if _, err := fmt.Fprintf(out, "%s\nType %q to confirm: ", message, target); err != nil {
		return err
	}
	line, err := bufio.NewReader(in).ReadString('\n')
	if err != nil {
		return fmt.Errorf("operation cancelled: %w", err)
	}
	if strings.TrimSuffix(strings.TrimSuffix(line, "\n"), "\r") != target {
		return fmt.Errorf("operation cancelled: name did not match: %w", errUsage)
	}
	return nil
}
