// Package replication owns the replication views shared by usage and databases.
package replication

import (
	"context"
	"errors"
	"fmt"
	"net/url"
	"slices"
	"strings"
	"time"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/ui/charts"
)

type Region struct {
	core.ReplicationRegion
	DatabaseName string
}

type Pair struct {
	core.ReplicationPair
	DatabaseName string
}

type View struct {
	ProjectID, DatabaseID                   string
	Range, Error, Empty                     string
	Regions                                 []Region
	Pairs                                   []Pair
	Directions                              []core.ReplicationPair
	SentBytes, ReceivedBytes, ReplicatedOps int64
	WorstLagMS                              *float64
	Healthy, Lagging, Diverged, Stopped     int
	Chart                                   charts.LineData
}

func Load(ctx context.Context, deps core.Deps, p core.Principal, projectID, databaseID, rangeKey string) View {
	v := View{Range: core.MetricsRange(rangeKey), Empty: "No replication measurements for this period."}
	project, scope, err := deps.MetricsSelection(ctx, p, projectID, databaseID, "", rangeKey)
	if err != nil {
		v.Error = "Replication scope is unavailable."
		return v
	}
	v.ProjectID, v.DatabaseID = project.ID, databaseID
	if deps.Metrics == nil {
		v.Empty = "Replication measurements are unavailable."
		return v
	}
	ctx, cancel := context.WithTimeout(ctx, 5*time.Second)
	defer cancel()
	result, err := deps.Metrics.Replication(ctx, core.UserToken(ctx), scope)
	if errors.Is(err, core.ErrNotMetered) {
		return v
	}
	if err != nil {
		v.Error = "Could not load replication measurements. Retry this page."
		return v
	}
	names := make(map[string]string)
	for _, db := range project.Databases {
		names[db.ID] = db.Name
		if db.TerraBaseID != "" {
			names[db.TerraBaseID] = db.Name
		}
	}
	for _, row := range result.Regions {
		v.Regions = append(v.Regions, Region{ReplicationRegion: row, DatabaseName: databaseName(names, row.DatabaseID)})
		v.ReplicatedOps += row.ReplicatedOps
		if row.LagMS != nil && (v.WorstLagMS == nil || *row.LagMS > *v.WorstLagMS) {
			v.WorstLagMS = row.LagMS
		}
		switch row.Status {
		case "healthy":
			v.Healthy++
		case "lagging":
			v.Lagging++
		case "diverged":
			v.Diverged++
		case "stopped":
			v.Stopped++
		}
	}
	var starts []time.Time
	totals := make(map[[2]string]core.ReplicationPair)
	for _, row := range result.Pairs {
		v.Pairs = append(v.Pairs, Pair{ReplicationPair: row, DatabaseName: databaseName(names, row.DatabaseID)})
		v.SentBytes += row.SentBytes
		v.ReceivedBytes += row.ReceivedBytes
		key := [2]string{row.FromRegion, row.ToRegion}
		total := totals[key]
		total.FromRegion, total.ToRegion = row.FromRegion, row.ToRegion
		total.SentBytes += row.SentBytes
		total.ReceivedBytes += row.ReceivedBytes
		totals[key] = total
		for _, point := range row.Series {
			starts = append(starts, point.Start.UTC())
		}
	}
	for _, total := range totals {
		v.Directions = append(v.Directions, total)
	}
	slices.SortFunc(v.Directions, func(a, b core.ReplicationPair) int {
		return strings.Compare(a.FromRegion+"/"+a.ToRegion, b.FromRegion+"/"+b.ToRegion)
	})
	slices.SortFunc(v.Regions, func(a, b Region) int {
		return strings.Compare(a.DatabaseName+"/"+a.Region, b.DatabaseName+"/"+b.Region)
	})
	slices.SortFunc(v.Pairs, func(a, b Pair) int {
		return strings.Compare(a.DatabaseName+"/"+a.FromRegion+"/"+a.ToRegion, b.DatabaseName+"/"+b.FromRegion+"/"+b.ToRegion)
	})
	slices.SortFunc(starts, func(a, b time.Time) int { return a.Compare(b) })
	starts = slices.Compact(starts)
	labels := make([]string, len(starts))
	indices := make(map[time.Time]int, len(starts))
	for i, start := range starts {
		labels[i] = start.UTC().Format("Jan 2 15:04 UTC")
		indices[start] = i
	}
	directions := make(map[string][]int64)
	for _, row := range result.Pairs {
		name := row.FromRegion + " → " + row.ToRegion + " sent bytes"
		if directions[name] == nil {
			directions[name] = make([]int64, len(starts))
		}
		for _, point := range row.Series {
			directions[name][indices[point.Start.UTC()]] += point.SentBytes
		}
	}
	namesOfPairs := make([]string, 0, len(directions))
	for name := range directions {
		namesOfPairs = append(namesOfPairs, name)
	}
	slices.Sort(namesOfPairs)
	series := make([]charts.Series, 0, len(directions))
	for _, name := range namesOfPairs {
		series = append(series, charts.Series{Name: name, Values: directions[name]})
	}
	v.Chart = charts.NewLine("Replication bandwidth sent by region pair (bytes), "+v.Range, charts.TimeTicks(starts, v.Range), labels, 200, series...)
	return v
}

func databaseName(names map[string]string, id string) string {
	if name := names[id]; name != "" {
		return name
	}
	return id
}

func StatusTone(status string) string {
	switch status {
	case "healthy", "operational":
		return "success"
	case "lagging", "degraded":
		return "warning"
	case "diverged", "stopped", "down":
		return "danger"
	default:
		return "neutral"
	}
}

func applied(t *time.Time) string {
	if t == nil || t.IsZero() {
		return "Not reported"
	}
	return t.UTC().Format("Jan 2, 2006 15:04:05 UTC")
}

func milliseconds(n *float64) string {
	if n == nil {
		return "Not reported"
	}
	return fmt.Sprintf("%g ms", *n)
}

func (v View) analyticsURL() string {
	return "/analytics?" + url.Values{"project": {v.ProjectID}, "database": {v.DatabaseID}, "range": {v.Range}}.Encode()
}

func divergence(n *int64) string {
	if n == nil {
		return "Not reported"
	}
	return fmt.Sprint(*n)
}
