package home

import (
	"context"
	"fmt"
	"slices"
	"time"

	"terrascale.tech/dashboard/internal/core"
)

type regionRow struct {
	Code, Status, Latency string
	Databases             int
}
type regionsView struct {
	Rows  []regionRow
	Error string
}

func loadRegions(ctx context.Context, deps core.Deps, p core.Principal) regionsView {
	v := regionsView{}
	a, err := deps.Store.SnapshotAs(ctx, p)
	if err != nil {
		v.Error = "Could not load organization regions."
		return v
	}
	org, err := a.OrganizationFor(p, p.OrganizationID)
	if err != nil {
		v.Error = "Organization regions are unavailable."
		return v
	}
	counts := make(map[string]int)
	for _, project := range org.Projects {
		if project.OrganizationID != org.ID {
			continue
		}
		for _, db := range project.Databases {
			codes := slices.Clone(db.Regions)
			codes = append(codes, db.Region)
			slices.Sort(codes)
			codes = slices.Compact(codes)
			for _, code := range codes {
				if code != "" {
					counts[code]++
				}
			}
		}
	}
	codes := make([]string, 0, len(counts))
	for code := range counts {
		codes = append(codes, code)
	}
	slices.Sort(codes)
	health := make(map[string]core.RegionHealth)
	if deps.RegionHealth != nil && len(codes) > 0 {
		ctx, cancel := context.WithTimeout(ctx, 5*time.Second)
		defer cancel()
		measurements, err := deps.RegionHealth.RegionHealth(ctx, codes)
		if err != nil {
			v.Error = "Could not load region health and latency. Retry this page."
		} else {
			for _, row := range measurements {
				health[row.Region] = row
			}
		}
	}
	for _, code := range codes {
		row := regionRow{Code: code, Databases: counts[code], Status: "Not reported", Latency: "Not reported"}
		if measurement, ok := health[code]; ok {
			if measurement.Status != "" {
				row.Status = measurement.Status
			}
			if measurement.LatencyMS != nil {
				row.Latency = fmt.Sprintf("%.2f ms", *measurement.LatencyMS)
			}
		}
		v.Rows = append(v.Rows, row)
	}
	return v
}
