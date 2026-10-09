// Package analytics owns project time-series reporting and native GET filters.
package analytics

import (
	"context"
	"errors"
	"fmt"
	"math"
	"net/http"
	"slices"
	"strings"
	"time"

	"github.com/a-h/templ"
	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/ui/charts"
)

type Chart struct {
	Title string
	Data  charts.LineData
}
type Option struct{ ID, Name string }
type View struct {
	Range, ProjectID, DatabaseID, Region, Error, Empty                        string
	Databases                                                                 []Option
	Regions                                                                   []string
	Charts                                                                    []Chart
	Available                                                                 bool
	Requests, Operations, Errors, StorageBytes, ClientBytes, ReplicationBytes int64
	LatestP95MS                                                               *float64
	databaseNames                                                             map[string]string
}

func Mount(mux *http.ServeMux, deps core.Deps) {
	mux.HandleFunc("GET /analytics", func(w http.ResponseWriter, r *http.Request) {
		p, ok := deps.Sessions.Principal(r)
		if !ok {
			core.RedirectToLogin(w, r)
			return
		}
		q := r.URL.Query()
		v, err := Load(r.Context(), deps, p, q.Get("project"), q.Get("database"), q.Get("region"), q.Get("range"))
		if err != nil {
			if !core.WriteAccessError(w, err) {
				http.Error(w, "invalid analytics filter", http.StatusBadRequest)
			}
			return
		}
		page := core.Page{Layout: "shell", Title: "Analytics", Principal: p, View: func(core.Page) templ.Component { return pageView(v) }}
		if err := deps.Render.Page(w, r, page); err != nil {
			http.Error(w, "could not render analytics", http.StatusInternalServerError)
		}
	})
}

func Load(ctx context.Context, deps core.Deps, p core.Principal, projectID, databaseID, region, rangeKey string) (View, error) {
	project, scope, err := deps.MetricsSelection(ctx, p, projectID, databaseID, "", rangeKey)
	if err != nil {
		return View{}, err
	}
	v := View{Range: scope.Range, ProjectID: project.ID, DatabaseID: databaseID, Region: region, Empty: "No analytics measurements for these filters."}
	v.databaseNames = make(map[string]string)
	regionInProject := false
	for _, db := range project.Databases {
		v.databaseNames[db.ID] = db.Name
		if db.TerraBaseID != "" {
			v.databaseNames[db.TerraBaseID] = db.Name
		}
		v.Databases = append(v.Databases, Option{ID: db.ID, Name: db.Name})
		regionInProject = regionInProject || db.Region == region || slices.Contains(db.Regions, region)
		if databaseID == "" || db.ID == databaseID {
			v.Regions = append(v.Regions, db.Region)
			v.Regions = append(v.Regions, db.Regions...)
		}
	}
	slices.SortFunc(v.Databases, func(a, b Option) int { return strings.Compare(a.Name, b.Name) })
	slices.Sort(v.Regions)
	v.Regions = slices.Compact(v.Regions)
	v.Regions = slices.DeleteFunc(v.Regions, func(s string) bool { return s == "" })
	if region != "" && !slices.Contains(v.Regions, region) {
		if databaseID == "" || !regionInProject {
			return View{}, core.Invalid("select a region used by this database or project")
		}
		// A native database change can carry a region from the previous selection.
		region = ""
	}
	v.Region, scope.Region = region, region
	if deps.Metrics == nil {
		v.Empty = "Analytics measurements are unavailable."
		return v, nil
	}
	ctx, cancel := context.WithTimeout(ctx, 5*time.Second)
	defer cancel()
	result, err := deps.Metrics.Analytics(ctx, core.UserToken(ctx), scope)
	if errors.Is(err, core.ErrNotMetered) {
		return v, nil
	}
	if err != nil {
		v.Error = "Could not load analytics measurements. Apply the filters to retry."
		return v, nil
	}
	if len(result.Series) == 0 {
		return v, nil
	}
	build(&v, result.Series)
	return v, nil
}

func build(v *View, points []core.AnalyticsPoint) {
	var starts []time.Time
	for _, point := range points {
		starts = append(starts, point.Start.UTC())
	}
	slices.SortFunc(starts, func(a, b time.Time) int { return a.Compare(b) })
	starts = slices.Compact(starts)
	if len(starts) == 0 {
		return
	}
	v.Available = true
	labels := make([]string, len(starts))
	indices := make(map[time.Time]int, len(starts))
	for i, start := range starts {
		labels[i] = start.Format("Jan 2 15:04 UTC")
		indices[start] = i
	}
	errors, storage, sent, received, replication := make([]int64, len(starts)), make([]int64, len(starts)), make([]int64, len(starts)), make([]int64, len(starts)), make([]int64, len(starts))
	requestTypes, opTypes := make(map[string][]int64), make(map[string][]int64)
	latencies := make(map[string]charts.Series)
	for _, p := range points {
		i := indices[p.Start.UTC()]
		for name, count := range p.Requests {
			if requestTypes[name] == nil {
				requestTypes[name] = make([]int64, len(starts))
			}
			requestTypes[name][i] += count
			v.Requests += count
		}
		for name, count := range p.Operations {
			if opTypes[name] == nil {
				opTypes[name] = make([]int64, len(starts))
			}
			opTypes[name][i] += count
			v.Operations += count
		}
		errors[i] += p.Errors
		storage[i] += p.StorageBytes
		sent[i] += p.ClientSentBytes
		received[i] += p.ClientReceivedBytes
		replication[i] += p.ReplicationBytes
		v.Errors += p.Errors
		v.ClientBytes += p.ClientSentBytes + p.ClientReceivedBytes
		v.ReplicationBytes += p.ReplicationBytes
		group := p.DatabaseID
		if name := v.databaseNames[group]; name != "" {
			group = name
		}
		if p.Region != "" {
			group += " / " + p.Region
		}
		for _, percentile := range []struct {
			name  string
			value *float64
		}{{"p50", p.P50LatencyMS}, {"p95", p.P95LatencyMS}, {"p99", p.P99LatencyMS}} {
			if percentile.value == nil {
				continue
			}
			name := strings.TrimSpace(percentile.name + " " + group)
			series, ok := latencies[name]
			if !ok {
				series = charts.Series{Name: name, Values: make([]int64, len(starts)), Missing: make([]bool, len(starts))}
				for j := range series.Missing {
					series.Missing[j] = true
				}
			}
			series.Values[i] = int64(math.Round(*percentile.value * 1000))
			series.Missing[i] = false
			latencies[name] = series
		}
		if i == len(starts)-1 && p.P95LatencyMS != nil && (v.LatestP95MS == nil || *p.P95LatencyMS > *v.LatestP95MS) {
			v.LatestP95MS = p.P95LatencyMS
		}
	}
	v.StorageBytes = storage[len(storage)-1]
	add := func(title string, series ...charts.Series) {
		v.Charts = append(v.Charts, Chart{Title: title, Data: charts.NewLine(title+", "+v.Range, charts.TimeTicks(starts, v.Range), labels, 200, series...)})
	}
	add("Requests by type", typedSeries(requestTypes)...)
	add("Operations by type", typedSeries(opTypes)...)
	add("Errors", charts.Series{Name: "Errors", Values: errors})
	latencyNames := make([]string, 0, len(latencies))
	for name := range latencies {
		latencyNames = append(latencyNames, name)
	}
	slices.Sort(latencyNames)
	latencySeries := make([]charts.Series, 0, len(latencies))
	for _, name := range latencyNames {
		latencySeries = append(latencySeries, latencies[name])
	}
	add("Latency percentiles (µs)", latencySeries...)
	add("Storage (bytes)", charts.Series{Name: "Storage", Values: storage})
	add("Client bandwidth (bytes)", charts.Series{Name: "Sent", Values: sent}, charts.Series{Name: "Received", Values: received})
	add("Replication bandwidth (bytes)", charts.Series{Name: "Replication bytes", Values: replication})
}

func typedSeries(types map[string][]int64) []charts.Series {
	names := make([]string, 0, len(types))
	for name := range types {
		names = append(names, name)
	}
	slices.Sort(names)
	series := make([]charts.Series, 0, len(names))
	for _, name := range names {
		series = append(series, charts.Series{Name: name, Values: types[name]})
	}
	return series
}

func latency(ms *float64) string {
	if ms == nil {
		return "Not reported"
	}
	return fmt.Sprintf("%.3f ms", *ms)
}
