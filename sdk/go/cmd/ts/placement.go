package main

import (
	"context"
	"errors"
	"fmt"
	"io"
	"strings"

	v1 "terrascale.tech/sdk/v1"
)

func validRegion(s string) bool {
	return len(s) > 0 && len(s) <= 63 && s[0] >= 'a' && s[0] <= 'z' && s[len(s)-1] != '-' && !strings.ContainsFunc(s, func(r rune) bool {
		return !(r >= 'a' && r <= 'z' || r >= '0' && r <= '9' || r == '-')
	})
}

func placementOptions(name string, regionsSet bool, regions string, all, allSet bool) (v1.DatabasePlacement, error) {
	var p v1.DatabasePlacement
	if !regionsSet && !allSet {
		if name == "db placement" {
			return p, fmt.Errorf("db placement needs -regions or -all-regions: %w", errUsage)
		}
		return p, nil
	}
	if name != "db create" && name != "db placement" {
		return p, fmt.Errorf("-regions and -all-regions need db create or db placement: %w", errUsage)
	}
	if regionsSet && allSet || allSet && !all {
		return p, fmt.Errorf("use -regions or -all-regions, not both: %w", errUsage)
	}
	if allSet {
		p.AllRegions = true
		return p, nil
	}
	p.Regions = []string{}
	if regions == "" {
		return p, fmt.Errorf("-regions needs at least one region name: %w", errUsage)
	}
	seen := map[string]bool{}
	for r := range strings.SplitSeq(regions, ",") {
		if !validRegion(r) || seen[r] {
			return p, fmt.Errorf("-regions needs distinct comma-separated region names: %w", errUsage)
		}
		seen[r] = true
		p.Regions = append(p.Regions, r)
	}
	if len(p.Regions) > 9 {
		return p, fmt.Errorf("-regions accepts at most nine region ids: %w", errUsage)
	}
	return p, nil
}

func createPlaced(ctx context.Context, tb *v1.Client, project, name string, p v1.DatabasePlacement, out io.Writer) error {
	result, err := tb.CreateDatabase(ctx, project, name, v1.CreateDatabaseOptions{Regions: p.Regions, AllRegions: p.AllRegions})
	if err != nil {
		return err
	}
	return emitJSON(out, result)
}

func setPlacement(ctx context.Context, tb *v1.Client, project, database string, p v1.DatabasePlacement, out io.Writer) error {
	result, err := tb.SetDatabasePlacement(ctx, project, database, p)
	if errors.Is(err, v1.ErrReplicaNotReady) {
		return fmt.Errorf("requested regions remain selected; no requested region is ready to order writes yet. Run the same db placement command again after replication (about 30 seconds for a new region): %w", err)
	}
	if errors.Is(err, v1.ErrMoveInProgress) {
		return fmt.Errorf("a different placement change is moving this database; wait for it to finish before changing the requested regions: %w", err)
	}
	if err != nil {
		return err
	}
	return emitJSON(out, result)
}

func placementWriter(ctx context.Context, tb *v1.Client, project, database string) (string, error) {
	status, err := tb.Database(project, database).Regions(ctx)
	if err != nil {
		return "", err
	}
	for _, region := range status.Regions {
		if region.Role == "writer" {
			return region.Region, nil
		}
	}
	return "", errors.New("database regions did not identify the region ordering writes")
}

func createEarlierPlaced(ctx context.Context, tb *v1.Client, project, name, writer string, replicas []string, all bool, out io.Writer) error {
	var selected []string
	if writer != "" {
		selected = append([]string{writer}, replicas...)
	}
	created, err := tb.CreateDatabase(ctx, project, name, v1.CreateDatabaseOptions{Regions: selected})
	if err != nil {
		return err
	}
	placementFailure := func(cause error, flags string) error {
		return fmt.Errorf("database %s was created; its placement may not be complete: %w. Run `ts db placement %s %s` again; repeating it is safe.", name, cause, name, flags)
	}
	if writer == "" && len(replicas) != 0 {
		flags := "-replica-regions " + strings.Join(replicas, ",")
		writer, err = placementWriter(ctx, tb, project, created.ID)
		if err != nil {
			return placementFailure(err, flags)
		}
		if err = setPlacement(ctx, tb, project, created.ID, v1.DatabasePlacement{Regions: append([]string{writer}, replicas...)}, out); err != nil {
			return placementFailure(err, flags)
		}
		return nil
	}
	if all {
		if err = setPlacement(ctx, tb, project, created.ID, v1.DatabasePlacement{AllRegions: true}, out); err != nil {
			return placementFailure(err, "-all-regions")
		}
		return nil
	}
	return emitJSON(out, created)
}

func setEarlierPlacement(ctx context.Context, tb *v1.Client, project, database string, replicas []string, out io.Writer) error {
	writer, err := placementWriter(ctx, tb, project, database)
	if err != nil {
		return err
	}
	return setPlacement(ctx, tb, project, database, v1.DatabasePlacement{Regions: append([]string{writer}, replicas...)}, out)
}
