package main

import (
	"context"
	"fmt"
	"io"
	"strings"

	v1 "terrascale.tech/sdk/v1"
)

// placementRequest accepts customer data-placement intent. It does not enable
// a capability or choose a service origin; the server checks serving regions.
func placementRequest(home, replicas, placement string, replicasSet bool) (v1.Placement, error) {
	p := v1.Placement{HomeRegion: home}
	if placement != "" && placement != "all" {
		return p, fmt.Errorf("-placement must be all, or omitted for server-selected placement: %w", errUsage)
	}
	p.AllRegions = placement == "all"
	if p.AllRegions && replicasSet {
		return p, fmt.Errorf("-placement all cannot be combined with -replica-regions: %w", errUsage)
	}
	validRegion := func(s string) bool {
		return len(s) > 0 && len(s) <= 63 && s[0] >= 'a' && s[0] <= 'z' && s[len(s)-1] != '-' && !strings.ContainsFunc(s, func(r rune) bool {
			return !(r >= 'a' && r <= 'z' || r >= '0' && r <= '9' || r == '-')
		})
	}
	if home != "" && !validRegion(home) {
		return p, fmt.Errorf("-home-region must be a region name: %w", errUsage)
	}
	if replicasSet {
		p.ReplicaRegions = []string{}
		if replicas != "none" {
			seen := make(map[string]bool)
			for _, region := range strings.Split(replicas, ",") {
				region = strings.TrimSpace(region)
				if !validRegion(region) || region == home || seen[region] {
					return p, fmt.Errorf("-replica-regions needs distinct region names other than the home, or none: %w", errUsage)
				}
				seen[region] = true
				p.ReplicaRegions = append(p.ReplicaRegions, region)
			}
		}
	}
	return p, nil
}

func createPlacedDatabase(ctx context.Context, db *v1.Database, placement v1.Placement, out io.Writer) error {
	result, err := db.CreateIn(ctx, placement)
	if err != nil {
		return err
	}
	return emitJSON(out, result)
}
