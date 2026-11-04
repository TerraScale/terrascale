package main

import (
	"context"
	"fmt"
	"io"
	"strings"
	"time"

	v1 "terrascale.tech/sdk/v1"
)

func validRegion(s string) bool {
	return len(s) > 0 && len(s) <= 63 && s[0] >= 'a' && s[0] <= 'z' && s[len(s)-1] != '-' && !strings.ContainsFunc(s, func(r rune) bool {
		return !(r >= 'a' && r <= 'z' || r >= '0' && r <= '9' || r == '-')
	})
}

func placementOptions(name string, operands []string, replicasSet bool, replicas string, all, allSet bool) (v1.DatabasePlacement, error) {
	var p v1.DatabasePlacement
	if name == "db move-home" && !validRegion(operands[1]) {
		return p, fmt.Errorf("move-home needs a region name: %w", errUsage)
	}
	if !replicasSet && !allSet {
		if name == "db placement" {
			return p, fmt.Errorf("db placement needs -replica-regions or -all-regions: %w", errUsage)
		}
		return p, nil
	}
	if name != "db create" && name != "db placement" {
		return p, fmt.Errorf("-replica-regions and -all-regions need db create or db placement: %w", errUsage)
	}
	if replicasSet && allSet || allSet && !all {
		return p, fmt.Errorf("use -replica-regions or -all-regions, not both: %w", errUsage)
	}
	if allSet {
		p.AllRegions = true
		return p, nil
	}
	p.ReplicaRegions = []string{}
	if replicas == "" {
		return p, nil
	}
	for r := range strings.SplitSeq(replicas, ",") {
		if !validRegion(r) {
			return p, fmt.Errorf("-replica-regions needs comma-separated region names: %w", errUsage)
		}
		p.ReplicaRegions = append(p.ReplicaRegions, r)
	}
	return p, nil
}

func createPlaced(ctx context.Context, tb *v1.Client, project, name, home string, p v1.DatabasePlacement, out io.Writer) error {
	result, err := tb.CreateDatabase(ctx, project, name, v1.CreateDatabaseOptions{HomeRegion: home, ReplicaRegions: p.ReplicaRegions, AllRegions: p.AllRegions})
	if err != nil {
		return err
	}
	return emitJSON(out, result)
}

func setPlacement(ctx context.Context, tb *v1.Client, project, database string, p v1.DatabasePlacement, out io.Writer) error {
	result, err := tb.SetDatabasePlacement(ctx, project, database, p)
	if err != nil {
		return err
	}
	return emitJSON(out, result)
}

func moveHome(ctx context.Context, tb *v1.Client, project, database, region string, out, stderr io.Writer) error {
	start := time.Now()
	result, err := tb.MoveDatabaseHome(ctx, project, database, region)
	if err != nil {
		return err
	}
	fmt.Fprintf(stderr, "move-home took %d ms\n", time.Since(start).Milliseconds())
	return emitJSON(out, result)
}
