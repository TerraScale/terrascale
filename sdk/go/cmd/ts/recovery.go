package main

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os"
	"time"

	v1 "terrascale.tech/sdk/v1"
)

func recovery(ctx context.Context, db *v1.Database, command string, args []string, out io.Writer) error {
	if command == "recovery preview" {
		at, err := time.Parse(time.RFC3339, args[0])
		if err != nil {
			return errors.New("recovery time must be RFC 3339")
		}
		var selections []v1.RecoverySelection
		if err := readRecoveryJSON(args[1], &selections); err != nil {
			return err
		}
		preview, err := db.PreviewSelectiveRecovery(ctx, v1.RecoveryPreviewRequest{PointInTime: at, Selections: selections})
		if err != nil {
			return err
		}
		return emitJSON(out, preview)
	}
	var preview v1.RecoveryPreview
	if err := readRecoveryJSON(args[0], &preview); err != nil {
		return err
	}
	var approved []v1.RecoveryApproval
	if err := readRecoveryJSON(args[1], &approved); err != nil {
		return err
	}
	if preview.Approval == "" || !preview.ExpiresAt.After(time.Now()) || len(approved) == 0 {
		return errors.New("recovery requires a fresh preview and an explicit nonempty approval list")
	}
	available := make(map[v1.RecoveryApproval]bool, len(preview.Records))
	for _, record := range preview.Records {
		available[v1.RecoveryApproval{Collection: record.Collection, ID: record.ID}] = true
	}
	for _, record := range approved {
		if !available[record] {
			return errors.New("approved records must be a unique subset of the preview")
		}
		delete(available, record)
	}
	result, err := db.RecoverHistoricalRecords(ctx, v1.RecoveryRequest{Approval: preview.Approval, Records: approved})
	if err != nil {
		return err
	}
	return emitJSON(out, result)
}

func readRecoveryJSON(path string, out any) error {
	f, err := os.Open(path)
	if err != nil {
		return err
	}
	defer f.Close()
	info, err := f.Stat()
	if err != nil {
		return err
	}
	if !info.Mode().IsRegular() || info.Size() > 8<<20 {
		return errors.New("recovery input must be a regular JSON file of at most 8 MiB")
	}
	decoder := json.NewDecoder(io.LimitReader(f, 8<<20))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(out); err != nil {
		return fmt.Errorf("invalid recovery JSON file: %w", err)
	}
	if err := decoder.Decode(new(any)); !errors.Is(err, io.EOF) {
		return errors.New("recovery file must contain exactly one JSON value")
	}
	return nil
}
