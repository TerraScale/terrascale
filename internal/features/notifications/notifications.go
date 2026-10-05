// Package notifications owns the account-scoped notification inbox.
package notifications

import (
	"net/http"
	"strconv"

	"github.com/a-h/templ"

	"terrascale.tech/dashboard/internal/core"
)

const pageSize = 10

type pageData struct {
	Notifications []core.Notification
	Status        string
	Unread        int
	Critical      int
	Total         int
	FilteredCount int
	Page          int
	PageCount     int
	Previous      int
	Next          int
	HasPrevious   bool
	HasNext       bool
}

// Mount registers the notification inbox, its read mutations, and tools.
func Mount(mux *http.ServeMux, deps core.Deps) {
	service := Service{store: deps.Store}
	mountTools(deps.Tools, service)
	mux.HandleFunc("GET /notifications", func(w http.ResponseWriter, r *http.Request) {
		p, ok := principal(deps, w, r)
		if !ok {
			return
		}
		data, err := service.Inbox(r.Context(), p, r.URL.Query().Get("status"), r.URL.Query().Get("page"))
		if err != nil {
			writeError(w, err)
			return
		}
		render(w, r, deps, p, data)
	})

	mux.HandleFunc("POST /api/v1/notifications/{notificationID}/read", func(w http.ResponseWriter, r *http.Request) {
		if !deps.VerifyCSRF(w, r) {
			return
		}
		p, ok := principal(deps, w, r)
		if !ok {
			return
		}
		if _, err := service.MarkRead(r.Context(), p, r.PathValue("notificationID")); err != nil {
			writeError(w, err)
			return
		}
		http.Redirect(w, r, returnHref(r), http.StatusSeeOther)
	})

	mux.HandleFunc("POST /api/v1/notifications/read-all", func(w http.ResponseWriter, r *http.Request) {
		if !deps.VerifyCSRF(w, r) {
			return
		}
		p, ok := principal(deps, w, r)
		if !ok {
			return
		}
		if _, err := service.MarkAllRead(r.Context(), p); err != nil {
			writeError(w, err)
			return
		}
		http.Redirect(w, r, returnHref(r), http.StatusSeeOther)
	})
}

func principal(deps core.Deps, w http.ResponseWriter, r *http.Request) (core.Principal, bool) {
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

func render(w http.ResponseWriter, r *http.Request, deps core.Deps, p core.Principal, data pageData) {
	page := core.Page{
		Layout: "shell", Title: "Notifications", Principal: p,
		View: func(view core.Page) templ.Component { return notificationsView(data, view.CSRFToken) },
	}
	if err := deps.Render.Page(w, r, page); err != nil {
		http.Error(w, "could not render notifications", http.StatusInternalServerError)
	}
}

func writeError(w http.ResponseWriter, err error) {
	if !core.WriteAccessError(w, err) {
		http.Error(w, "invalid request", http.StatusBadRequest)
	}
}

// returnHref is the inbox view a mark-read form was posted from, so readers
// keep their filter and page.
func returnHref(r *http.Request) string {
	page, _ := strconv.Atoi(r.PostFormValue("page"))
	return pageHref(page, r.PostFormValue("status"))
}
