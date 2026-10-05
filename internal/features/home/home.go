// Package home owns the authenticated account overview.
package home

import (
	"errors"
	"net/http"

	"github.com/a-h/templ"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/features/demovisuals"
	"terrascale.tech/dashboard/internal/features/notfound"
	"terrascale.tech/dashboard/internal/ui/charts"
)

type pageData struct {
	Tab                       string
	ProjectName               string
	ActivityRange             string
	ActivityNote              string
	ActivityTotalLabel        string
	ActivityChart             charts.LineData
	DatabaseDetail            string
	OrganizationMembersDetail string
	NotificationsDetail       string
	DatabaseCount             int
	OrganizationMembers       int
	UnreadNotifications       int
	TotalNotifications        int
	ActiveCredentials         int
	ActivityEvents            int64
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
		summary, err := service.Overview(r.Context(), p, r.URL.Query().Get("range"))
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
			ActivityNote: activity.Note, ActivityTotalLabel: activity.TotalLabel,
			ActivityChart: charts.NewLine("Activity, "+demovisuals.RangeLabel(activity.Range)+": "+activity.Note,
				demovisuals.TickLabels(activity.Range), demovisuals.PointLabels(activity.Range, len(activity.Points)), 180,
				charts.Series{Name: "Operations", Values: activity.Points}),
			DatabaseCount: summary.DatabaseCount, OrganizationMembers: summary.OrganizationMembers,
			UnreadNotifications: summary.UnreadNotifications, TotalNotifications: summary.TotalNotifications,
			ActiveCredentials: summary.ActiveCredentials, ActivityEvents: activity.Total,
			DatabaseDetail: "saved project databases", OrganizationMembersDetail: "active organization members",
			NotificationsDetail: "notifications loaded", MCPEndpoint: core.MCPEndpoint(r),
		}
		if data.DatabaseCount == 0 {
			data.DatabaseDetail = "none saved"
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
