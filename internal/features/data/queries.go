package data

import (
	"cmp"
	"errors"
	"net/http"
	"net/url"
	"strings"

	"terrascale.tech/dashboard/internal/core"
	sdk "terrascale.tech/sdk"
)

func mountQueryRoutes(mux *http.ServeMux, deps core.Deps, service Service) {
	mux.HandleFunc("POST /api/v1/dashboard/data/sql/check", func(w http.ResponseWriter, r *http.Request) {
		r.Body = http.MaxBytesReader(w, r.Body, maxSQLCheckBytes+4096)
		p, ok := deps.FormPrincipal(w, r)
		if !ok {
			return
		}
		ctx := r.Context()
		db, err := service.database(ctx, p, r.FormValue("databaseId"))
		if err != nil {
			writeError(w, err)
			return
		}
		statement := r.FormValue("sql")
		if len(statement) > maxSQLCheckBytes {
			http.Error(w, "SQL must be 16 KiB or less", http.StatusBadRequest)
			return
		}
		if !validCollection(strings.TrimSpace(r.FormValue("collection"))) {
			http.Error(w, "invalid collection", http.StatusBadRequest)
			return
		}
		renderSection(w, r, deps, db.ID, "sql", http.StatusOK, func(d *pageData) {
			d.SQL, d.CheckRequested = statement, true
		})
	})
	mux.HandleFunc("POST /api/v1/dashboard/data/sql", func(w http.ResponseWriter, r *http.Request) {
		r.Body = http.MaxBytesReader(w, r.Body, 32<<10)
		p, ok := deps.FormPrincipal(w, r)
		if !ok {
			return
		}
		ctx := r.Context()
		db, err := service.database(ctx, p, r.FormValue("databaseId"))
		if err != nil {
			writeError(w, err)
			return
		}
		statement := strings.TrimSpace(r.FormValue("sql"))
		failed := func(status int, message string) {
			formFailed(w, r, deps, db.ID, "sql", status, message, func(d *pageData) { d.SQL = statement })
		}
		if analysis := analyzeSQL(statement); !analysis.OK || sdk.ValidateSQL(statement, nil) != nil {
			failed(http.StatusBadRequest, cmp.Or(analysis.Error, "The console cannot bind parameters such as $1; replace them with values."))
			return
		}
		run, err := service.RunSQL(ctx, p, db.ID, statement)
		if err != nil {
			reason, invalid := core.InvalidReason(err)
			switch {
			case invalid:
				failed(http.StatusBadRequest, reason)
			case errors.Is(err, core.ErrDataPlane):
				failed(http.StatusServiceUnavailable, "The live data plane is unavailable. Try again shortly.")
			default:
				status, message := failure(err, "The query failed")
				failed(status, message)
			}
			return
		}
		if !core.WantsHTML(r) {
			core.WriteJSON(w, http.StatusOK, run.JSON)
			return
		}
		renderSection(w, r, deps, db.ID, "sql", http.StatusOK, func(d *pageData) {
			d.SQL, d.SQLResult = statement, &run.Table
		})
	})
	mux.HandleFunc("GET /api/v1/dashboard/data/saved-queries", func(w http.ResponseWriter, r *http.Request) {
		p, ok := principal(w, r, deps)
		if !ok {
			return
		}
		queries, err := service.SavedQueries(r.Context(), p)
		if err != nil {
			writeError(w, err)
			return
		}
		core.WriteJSON(w, http.StatusOK, queries)
	})
	mux.HandleFunc("POST /api/v1/dashboard/data/saved-queries", func(w http.ResponseWriter, r *http.Request) {
		p, ok := deps.FormPrincipal(w, r)
		if !ok {
			return
		}
		ctx := r.Context()
		if err := core.Can(ctx, deps.Store, p, core.Organization.CanWrite); err != nil {
			writeError(w, err)
			return
		}
		name, sql := strings.TrimSpace(r.FormValue("name")), strings.TrimSpace(r.FormValue("sql"))
		failed := func(status int, message string) {
			formFailed(w, r, deps, r.FormValue("databaseId"), "sql", status, message, func(d *pageData) {
				d.Dialog, d.QueryName, d.SQL = "save-query", name, sql
			})
		}
		if name == "" || len(name) > 128 {
			failed(http.StatusBadRequest, "Name the query in 128 characters or less.")
			return
		}
		if analysis := analyzeSQL(sql); !analysis.OK || sdk.ValidateSQL(sql, nil) != nil {
			failed(http.StatusBadRequest, cmp.Or(analysis.Error, "Saved queries cannot use parameters such as $1; replace them with values."))
			return
		}
		query, err := service.SaveQuery(ctx, p, name, sql)
		if err != nil {
			status, message := failure(err, "The query could not be saved")
			failed(status, message)
			return
		}
		if core.WantsHTML(r) {
			http.Redirect(w, r, savedQueriesReturn(r), http.StatusSeeOther)
			return
		}
		core.WriteJSON(w, http.StatusCreated, query)
	})
	mux.HandleFunc("POST /api/v1/dashboard/data/saved-queries/delete", func(w http.ResponseWriter, r *http.Request) {
		p, ok := deps.FormPrincipal(w, r)
		if !ok {
			return
		}
		ctx := r.Context()
		if err := core.Can(ctx, deps.Store, p, core.Organization.CanWrite); err != nil {
			writeError(w, err)
			return
		}
		if err := service.DeleteSavedQuery(ctx, p, r.FormValue("id")); err != nil {
			status, message := failure(err, "The query could not be deleted")
			if errors.Is(err, core.ErrNotFound) {
				message = "This saved query was already deleted."
			}
			formFailed(w, r, deps, r.FormValue("databaseId"), "sql", status, message, nil)
			return
		}
		if core.WantsHTML(r) {
			http.Redirect(w, r, savedQueriesReturn(r), http.StatusSeeOther)
			return
		}
		w.WriteHeader(http.StatusNoContent)
	})
}

// savedQueriesReturn sends a saved-query form back to the SQL console of the
// database and collection it was submitted from.
func savedQueriesReturn(r *http.Request) string {
	id := r.FormValue("databaseId")
	if id == "" {
		return "/databases"
	}
	var query url.Values
	if collection := strings.TrimSpace(r.FormValue("collection")); validCollection(collection) {
		query = url.Values{"collection": {collection}}
	}
	return sectionPath(id, "sql", query)
}
