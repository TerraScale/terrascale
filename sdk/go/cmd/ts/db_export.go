package main

import (
	"context"
	"errors"
	"fmt"
	"io"
	"io/fs"
	"os"
	"strings"
)

func dbExport(ctx context.Context, s *session, database, outputFile string, force bool, out io.Writer) error {
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
	if !force {
		if _, err := os.Stat(outputFile); err == nil {
			return fmt.Errorf("%s already exists: pass -force to overwrite: %w", outputFile, errUsage)
		} else if !errors.Is(err, fs.ErrNotExist) {
			return err
		}
	}
	partial := outputFile + ".partial"
	file, err := os.OpenFile(partial, os.O_WRONLY|os.O_CREATE|os.O_TRUNC, 0o600)
	if err != nil {
		return err
	}
	interrupted := func(cause error) error {
		file.Close()
		os.Remove(partial)
		return fmt.Errorf("export was interrupted and no file was written: %w", cause)
	}
	if _, err := io.Copy(file, body); err != nil {
		return interrupted(err)
	}
	if err := file.Sync(); err != nil {
		return interrupted(err)
	}
	committed, err := exportEndsCommitted(partial)
	if err != nil {
		return interrupted(err)
	}
	if !committed {
		return interrupted(errors.New("dump ends without COMMIT;"))
	}
	if err := file.Close(); err != nil {
		os.Remove(partial)
		return err
	}
	if err := os.Rename(partial, outputFile); err != nil {
		os.Remove(partial)
		return err
	}
	return nil
}

func exportEndsCommitted(path string) (bool, error) {
	file, err := os.Open(path)
	if err != nil {
		return false, err
	}
	defer file.Close()
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
