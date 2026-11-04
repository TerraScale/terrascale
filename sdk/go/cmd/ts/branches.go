package main

import (
	"context"
	"errors"
	"flag"
	"fmt"
	"io"
	"net/url"
	"regexp"
	"strconv"
	"strings"
	"text/tabwriter"
	"time"

	v1 "terrascale.tech/sdk/v1"
)

const previewTTL = int64(7 * 24 * 60 * 60)

var branchName = regexp.MustCompile(`^[a-z][a-z0-9_-]{0,62}$`)

func validateBranchOptions(command string, args []string, from, ttl string, fs *flag.FlagSet) (int64, error) {
	var supplied bool
	fs.Visit(func(f *flag.Flag) { supplied = supplied || f.Name == "from" || f.Name == "ttl" })
	if command != "db branch create" {
		if supplied {
			return 0, fmt.Errorf("--from and --ttl require db branch create: %w", errUsage)
		}
		return 0, nil
	}
	if !branchName.MatchString(from) || !branchName.MatchString(args[0]) || from == args[0] {
		return 0, fmt.Errorf("branch requires distinct valid source and target names and --from <database>: %w", errUsage)
	}
	if ttl == "" {
		return previewTTL, nil
	}
	// Go durations cover hours/minutes/seconds; days are useful for preview jobs.
	if days, ok := strings.CutSuffix(ttl, "d"); ok {
		n, err := strconv.ParseInt(days, 10, 64)
		if err != nil || n < 1 || n > 365 {
			return 0, fmt.Errorf("--ttl must be between 60s and 365d: %w", errUsage)
		}
		return n * 86400, nil
	}
	d, err := time.ParseDuration(ttl)
	if err != nil || d < time.Minute || d > 365*24*time.Hour || d%time.Second != 0 {
		return 0, fmt.Errorf("--ttl must be whole seconds between 60s and 365d: %w", errUsage)
	}
	return int64(d / time.Second), nil
}

func runBranch(ctx context.Context, s *session, command string, args []string, from string, ttl int64, expectedID string, out io.Writer) error {
	switch command {
	case "db branch create":
		source := s.tb.Database(s.project, from)
		if expectedID == "" {
			info, err := source.Info(ctx)
			if err != nil {
				return err
			}
			expectedID = info.ID
		}
		result, err := source.BranchIfIDWithOptions(ctx, args[0], expectedID, v1.ForkDatabaseOptions{TTLSeconds: &ttl})
		if err != nil {
			return err
		}
		if jsonMode(out) {
			return emitJSON(out, result)
		}
		_, err = fmt.Fprintf(out, "Created branch %s (%s)\n", result.Name, result.ID)
		return err
	case "db branch list":
		items := []v1.ManagedDatabase{}
		for item, err := range projectDatabases(ctx, s.tb, s.project, 100) {
			if err != nil {
				return err
			}
			if item.BranchedFrom != nil {
				items = append(items, item)
			}
		}
		if jsonMode(out) {
			return emitJSON(out, items)
		}
		w := tabwriter.NewWriter(out, 0, 0, 2, ' ', 0)
		fmt.Fprintln(w, "NAME\tID\tFROM\tEXPIRES")
		for _, item := range items {
			expires := "never"
			if item.ExpiresAt != nil {
				expires = item.ExpiresAt.UTC().Format(time.RFC3339)
			}
			fmt.Fprintf(w, "%s\t%s\t%s\t%s\n", item.Name, item.ID, *item.BranchedFrom, expires)
		}
		return w.Flush()
	case "db branch delete", "db branch url":
		db := s.tb.Database(s.project, args[0])
		info, err := db.Info(ctx)
		if err != nil {
			return err
		}
		if info.BranchedFrom == nil || *info.BranchedFrom == info.ID {
			return errors.New("database is not a branch")
		}
		if expectedID != "" && expectedID != info.ID {
			return &cliError{code: string(v1.ErrDatabaseIdentityMismatch), message: "branch identity changed", status: 409}
		}
		if command == "db branch delete" {
			return deleteExpectedDatabase(ctx, db, info.ID, out)
		}
		return branchConnectionURL(ctx, s, info, out)
	}
	return errUsage
}

func branchConnectionURL(ctx context.Context, s *session, info *v1.ManagedDatabase, out io.Writer) error {
	ttl := previewTTL
	if info.ExpiresAt != nil {
		ttl = min(ttl, int64(time.Until(*info.ExpiresAt)/time.Second))
	}
	if ttl < 60 {
		return errors.New("branch expires in less than 60 seconds; create a new branch")
	}
	key, err := s.tb.CreateScopedKey(ctx, v1.ScopedKeyRequest{
		Project: s.project, Database: info.ID, Access: v1.ScopedKeyReadWrite, TTLSeconds: &ttl,
	})
	if err != nil {
		return err
	}
	if key.Database != info.ID || key.ProjectID != s.project || key.Access != v1.ScopedKeyReadWrite {
		return errors.New("scoped key identity did not match the branch")
	}
	connection := url.URL{Scheme: "postgresql", User: url.UserPassword("any", key.Key), Host: "pg.terrascale.tech:5432", Path: "/" + s.project + "/" + info.ID,
		RawQuery: "sslmode=verify-full&sslrootcert=system"}
	if jsonMode(out) {
		return keyOutputError(emitJSON(out, struct {
			ConnectionURL string    `json:"connectionUrl"`
			ExpiresAt     time.Time `json:"expiresAt"`
		}{connection.String(), key.ExpiresAt}))
	}
	_, err = fmt.Fprintln(out, connection.String())
	return keyOutputError(err)
}
