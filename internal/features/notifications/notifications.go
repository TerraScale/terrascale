// Package notifications owns the account-scoped notification inbox.
package notifications

import (
	"net/http"
	"strconv"
	"strings"

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
		if patchDialog(w, r, deps, service, p) {
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
		if patchDialog(w, r, deps, service, p) {
			return
		}
		http.Redirect(w, r, returnHref(r), http.StatusSeeOther)
	})

	// The dialog body the shell's account-menu item and top-bar bell fetch
	// when they open the notifications dialog. Direct navigation shows the
	// full inbox page instead.
	mux.HandleFunc("GET /notifications/dialog", func(w http.ResponseWriter, r *http.Request) {
		p, ok := principal(deps, w, r)
		if !ok {
			return
		}
		// Datastar requests patch straight from patchDialog's own fetch;
		// fetching the inbox here too would double every dialog load.
		if strings.EqualFold(r.Header.Get("Datastar-Request"), "true") && deps.Events != nil {
			patchDialog(w, r, deps, service, p)
			return
		}
		data, err := service.Inbox(r.Context(), p, r.URL.Query().Get("status"), r.URL.Query().Get("page"))
		if err != nil {
			writeError(w, err)
			return
		}
		render(w, r, deps, p, data)
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

// patchDialog re-renders the notifications dialog through Datastar so opening
// it and marking notifications read never navigates away from the page. The
// inbox keeps the filter and page the request carried, in the query for
// fetches and the form for mark-read submissions. It reports whether the
// request wanted a patch; plain requests fall back to the redirect flow.
func patchDialog(w http.ResponseWriter, r *http.Request, deps core.Deps, service Service, p core.Principal) bool {
	if !strings.EqualFold(r.Header.Get("Datastar-Request"), "true") || deps.Events == nil {
		return false
	}
	status, page := r.URL.Query().Get("status"), r.URL.Query().Get("page")
	if r.Method == http.MethodPost {
		status, page = r.PostFormValue("status"), r.PostFormValue("page")
	}
	data, err := service.Inbox(r.Context(), p, status, page)
	if err != nil {
		writeError(w, err)
		return true
	}
	var fragment strings.Builder
	if err := dialogPatch(data, deps.CSRF.Token(w, r)).Render(r.Context(), &fragment); err != nil {
		http.Error(w, "could not render notifications", http.StatusInternalServerError)
		return true
	}
	if err := deps.Events.PatchElements(w, r, fragment.String()); err != nil {
		http.Error(w, "could not patch notifications", http.StatusInternalServerError)
	}
	return true
}

// returnHref is the inbox view a mark-read form was posted from, so readers
// keep their filter and page.
func returnHref(r *http.Request) string {
	page, _ := strconv.Atoi(r.PostFormValue("page"))
	return pageHref(page, r.PostFormValue("status"))
}
