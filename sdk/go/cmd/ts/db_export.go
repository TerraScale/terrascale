package main

import (
	"context"
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"strings"
)

func dbExport(ctx context.Context, s *session, database, outputFile string, force bool, out io.Writer) error {
	if outputFile != "" {
		if info, err := os.Lstat(outputFile); err == nil {
			if !info.Mode().IsRegular() {
				return fmt.Errorf("%s is not a regular file", outputFile)
			}
			if !force {
				return exportExists(outputFile)
			}
		} else if !errors.Is(err, os.ErrNotExist) {
			return err
		}
	}
	body, err := s.tb.ExportDatabase(ctx, s.project, database)
	if err != nil {
		return err
	}
	defer body.Close()
	if outputFile == "" {
		if _, err := io.Copy(out, body); err != nil {
			return fmt.Errorf("export was interrupted: %w", err)
		}
		return nil
	}
	file, err := os.CreateTemp(filepath.Dir(outputFile), ".terrascale-export-*")
	if err != nil {
		return err
	}
	defer os.Remove(file.Name())
	defer file.Close()
	interrupted := func(cause error) error {
		return fmt.Errorf("export was interrupted and no file was written: %w", cause)
	}
	if _, err := io.Copy(file, body); err != nil {
		return interrupted(err)
	}
	if err := file.Sync(); err != nil {
		return interrupted(err)
	}
	committed, err := exportEndsCommitted(file)
	if err != nil {
		return interrupted(err)
	}
	if !committed {
		return interrupted(errors.New("dump ends without COMMIT;"))
	}
	if err := file.Close(); err != nil {
		return err
	}
	if force {
		return os.Rename(file.Name(), outputFile)
	}
	if err := os.Link(file.Name(), outputFile); errors.Is(err, os.ErrExist) {
		return exportExists(outputFile)
	} else {
		return err
	}
}

func exportExists(path string) error {
	return fmt.Errorf("%s already exists: pass -force to overwrite: %w", path, errUsage)
}

func exportEndsCommitted(file *os.File) (bool, error) {
	info, err := file.Stat()
	if err != nil {
		return false, err
	}
	tail := make([]byte, min(64, info.Size()))
	if _, err := file.ReadAt(tail, info.Size()-int64(len(tail))); err != nil {
		return false, err
	}
	return strings.HasSuffix(strings.TrimSpace(string(tail)), "COMMIT;"), nil
}
