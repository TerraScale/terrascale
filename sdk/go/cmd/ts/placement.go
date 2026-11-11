package main

import (
	"context"
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
	if err != nil {
		return err
	}
	return emitJSON(out, result)
}
