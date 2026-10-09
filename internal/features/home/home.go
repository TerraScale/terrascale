// Package home owns the authenticated account overview.
package home

import (
	"errors"
	"fmt"
	"math"
	"net/http"
	"sync"
	"time"

	"github.com/a-h/templ"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/features/analytics"
	"terrascale.tech/dashboard/internal/features/demovisuals"
	"terrascale.tech/dashboard/internal/features/notfound"
	"terrascale.tech/dashboard/internal/features/replication"
	"terrascale.tech/dashboard/internal/ui/charts"
)

type pageData struct {
	Analytics                 analytics.View
	Replication               replication.View
	Regions                   regionsView
	Tab                       string
	ProjectName               string
	ActivityRange             string
	ActivitySample            bool
	ActivityNote              string
	ActivityChart             charts.LineData
	ActivityReads             int64
	ActivityWrites            int64
	DatabaseDetail            string
	OrganizationMembersDetail string
	NotificationsDetail       string
	DatabaseCount             int
	OrganizationMembers       int
	UnreadNotifications       int
	TotalNotifications        int
	ActiveCredentials         int
	MCPEndpoint               string
}

// Mount registers the dashboard overview.
func Mount(mux *http.ServeMux, deps core.Deps) {
	service := Service{deps: deps}
	mountTools(deps.Tools, service)
	mux.HandleFunc("GET /", func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/" {
			notfound.Render(w, r)
			return
		}
		p, ok := signedIn(deps, w, r)
		if !ok {
			return
		}
		summary, err := service.Overview(r.Context(), p, core.UserToken(r.Context()), core.MetricsRange(r.URL.Query().Get("range")))
		switch {
		case errors.Is(err, errFirstProjectNeeded):
			http.Redirect(w, r, "/organization?newOrganization=project", http.StatusSeeOther)
			return
		case errors.Is(err, errOrganizationUnavailable), errors.Is(err, errProjectUnavailable):
			http.Error(w, err.Error(), http.StatusForbidden)
			return
		case err != nil:
			writeError(w, err)
			return
		}
		tab := "overview"
		if r.URL.Query().Get("tab") == "connect" {
			tab = "connect"
		}
		activity := summary.Activity
		data := pageData{
			Tab: tab, ProjectName: summary.ProjectName, ActivityRange: activity.Range,
			ActivitySample: activity.Sample, ActivityNote: activity.Note, ActivityReads: activity.TotalReads, ActivityWrites: activity.TotalWrites,
			ActivityChart: activityChart(activity),
			DatabaseCount: summary.DatabaseCount, OrganizationMembers: summary.OrganizationMembers,
			UnreadNotifications: summary.UnreadNotifications, TotalNotifications: summary.TotalNotifications,
			ActiveCredentials: summary.ActiveCredentials,
			DatabaseDetail:    "saved project databases", OrganizationMembersDetail: "active organization members",
			NotificationsDetail: "notifications loaded", MCPEndpoint: core.MCPEndpoint(r),
		}
		if data.DatabaseCount == 0 {
			data.DatabaseDetail = "none saved"
		}
		if tab == "overview" {
			var reads sync.WaitGroup
			reads.Go(func() {
				view, loadErr := analytics.Load(r.Context(), deps, p, "", "", "", data.ActivityRange)
				if loadErr != nil {
					view = analytics.View{Error: "Analytics scope is unavailable."}
				}
				data.Analytics = view
			})
			reads.Go(func() { data.Replication = replication.Load(r.Context(), deps, p, "", "", data.ActivityRange) })
			reads.Go(func() { data.Regions = loadRegions(r.Context(), deps, p) })
			reads.Wait()
		}
		if data.OrganizationMembers == 1 {
			data.OrganizationMembersDetail = "active organization member"
		}
		page := core.Page{
			Layout: "shell", Title: "Overview", Principal: p,
			View: func(core.Page) templ.Component { return homeView(data) },
		}
		if err := deps.Render.Page(w, r, page); err != nil {
			http.Error(w, "could not render overview", http.StatusInternalServerError)
		}
	})
}

func signedIn(deps core.Deps, w http.ResponseWriter, r *http.Request) (core.Principal, bool) {
	if deps.Sessions == nil {
		http.Error(w, "authentication required", http.StatusUnauthorized)
		return core.Principal{}, false
	}
	p, ok := deps.Sessions.Principal(r)
	if !ok {
		core.RedirectToLogin(w, r)
		return core.Principal{}, false
	}
	return p, true
}

func writeError(w http.ResponseWriter, err error) {
	if !core.WriteAccessError(w, err) {
		http.Error(w, "invalid request", http.StatusBadRequest)
	}
}

// activityTicks labels the actual samples selected by charts.NewLine. Exact
// measured bucket timestamps remain available in the point tooltips.
func activityTicks(activity activity) []string {
	if activity.Sample {
		return demovisuals.TickLabels(activity.Range)
	}
	count := len(activity.Reads)
	if count == 0 || len(activity.bucketStarts) != count || activity.observedAt.IsZero() {
		return nil // NewLine falls back to the timestamp labels.
	}
	ticks := make([]string, 5)
	seen := make(map[int]bool)
	for step := range ticks {
		// Keep the indices aligned with NewLine's five evenly spaced ticks,
		// including short and sparse provider series.
		index := int(math.Round(float64(step) * float64(count-1) / 4))
		if count > 1 && index == count-1 && step != len(ticks)-1 {
			continue
		}
		if seen[index] {
			continue
		}
		seen[index] = true
		age := activity.observedAt.Sub(activity.bucketStarts[index])
		if age < 0 {
			return nil // Future timestamps must not be presented as current.
		}
		label := "Now"
		switch {
		case age >= 24*time.Hour:
			label = fmt.Sprintf("%gd", math.Round(age.Hours()/24*10)/10)
		case age >= time.Hour:
			label = fmt.Sprintf("%gh", math.Round(age.Hours()*10)/10)
		case age >= time.Minute:
			label = fmt.Sprintf("%dm", int(age.Minutes()))
		}
		if label != "Now" && (step == 0 || step == len(ticks)-1) {
			label += " ago"
		}
		ticks[step] = label
	}
	return ticks
}

// activityChart presents read and write counts as operations on the overview.
// The service retains the separate series for usage details and MCP clients.
func activityChart(activity activity) charts.LineData {
	label := "Operations"
	if activity.Sample {
		label = "Sample operations"
	}
	var operations []int64
	if len(activity.Reads) == len(activity.Writes) {
		operations = make([]int64, len(activity.Reads))
		for i := range operations {
			operations[i] = activity.Reads[i] + activity.Writes[i]
		}
	}
	series := []charts.Series{{Name: label, Values: operations}}
	rangeLabel := demovisuals.RangeLabel(activity.Range)
	if activity.Metered && activity.Range == "1h" {
		rangeLabel = "Current UTC hour"
	}
	return charts.NewLine(label+", "+rangeLabel+": "+activity.Note,
		activityTicks(activity), activity.Labels, 180, series...)
}
