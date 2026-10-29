package main

import (
	"context"
	"fmt"
	"io"
	"strconv"
	"strings"
	"time"

	v1 "terrascale.tech/sdk/v1"
)

func backupPolicyRequest(cron, retention string) (v1.BackupPolicyRequest, error) {
	days, err := strconv.Atoi(retention)
	if err != nil || days < 1 || days > 35 {
		return v1.BackupPolicyRequest{}, fmt.Errorf("retention must be an integer from 1 to 35 days: %w", errUsage)
	}
	req := v1.BackupPolicyRequest{RetentionDays: days}
	if cron != "none" {
		cron = strings.TrimSpace(cron)
		if len(strings.Fields(cron)) != 5 {
			return req, fmt.Errorf("cron must have five UTC fields, or be none to remove the custom schedule: %w", errUsage)
		}
		req.FullBackupCron = &cron // The producer validates cron grammar.
	}
	return req, nil
}

func backupSource(value string) (string, error) {
	switch value {
	case "", "platform":
		return "platform", nil
	case "customer":
		return value, nil
	default:
		return "", fmt.Errorf("destination must be platform or customer: %w", errUsage)
	}
}

func validateBackupSettings(command string, args []string) error {
	switch command {
	case "backup policy-set":
		_, err := backupPolicyRequest(args[0], args[1])
		return err
	case "backup window":
	case "backup plan", "backup restore-at":
		if _, err := time.Parse(time.RFC3339, args[0]); err != nil {
			return fmt.Errorf("restore time must be RFC3339: %w", errUsage)
		}
		args = args[1:]
		if command == "backup restore-at" {
			args = args[1:]
		}
	default:
		return nil
	}
	if len(args) > 0 {
		_, err := backupSource(args[0])
		return err
	}
	return nil
}

// backupSettings emits only SDK responses, and never infers a usable restore
// window from policy metadata. A plan is not an object-integrity check.
func backupSettings(ctx context.Context, db *v1.Database, command string, args []string, out io.Writer) error {
	switch command {
	case "backup policy":
		result, err := db.BackupPolicy(ctx)
		if err != nil {
			return err
		}
		return emitJSON(out, result)
	case "backup policy-set":
		req, err := backupPolicyRequest(args[0], args[1])
		if err != nil {
			return err
		}
		result, err := db.SetBackupPolicy(ctx, req)
		if err != nil {
			return err
		}
		return emitJSON(out, result)
	case "backup window", "backup plan", "backup restore-at":
		var at time.Time
		if command != "backup window" {
			var err error
			at, err = time.Parse(time.RFC3339, args[0])
			if err != nil {
				return fmt.Errorf("restore time must be RFC3339: %w", errUsage)
			}
			args = args[1:]
		}
		var restoreName string
		if command == "backup restore-at" {
			restoreName, args = args[0], args[1:]
		}
		source := ""
		if len(args) > 0 {
			source = args[0]
		}
		source, err := backupSource(source)
		if err != nil {
			return err
		}
		if command == "backup window" {
			result, err := db.RestoreWindow(ctx, source)
			if err != nil {
				return err
			}
			return emitJSON(out, result)
		}
		if command == "backup restore-at" {
			result, err := db.RestorePointInTime(ctx, v1.PointInTimeRequest{PointInTime: at, Name: restoreName, Destination: source})
			if err != nil {
				return err
			}
			return emitJSON(out, result)
		}
		result, err := db.PlanPointInTime(ctx, v1.PointInTimeRequest{PointInTime: at, Destination: source})
		if err != nil {
			return err
		}
		return emitJSON(out, result)
	}
	return errUsage
}
