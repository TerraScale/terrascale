package data

import (
	"cmp"
	json "encoding/json/v2"
	"errors"
	"maps"
	"math"
	"net/http"
	"net/url"
	"slices"
	"strconv"
	"strings"
	"time"

	"terrascale.tech/dashboard/internal/core"
	sdk "terrascale.tech/sdk"
)

func mountQueryRoutes(mux *http.ServeMux, deps core.Deps) {
	mux.HandleFunc("POST /api/v1/dashboard/data/sql/check", func(w http.ResponseWriter, r *http.Request) {
		if !deps.VerifyCSRF(w, r) {
			return
		}
		r.Body = http.MaxBytesReader(w, r.Body, maxSQLCheckBytes+4096)
		if err := r.ParseForm(); err != nil {
			http.Error(w, "invalid form", http.StatusBadRequest)
			return
		}
		_, db, err := selectedDB(deps, r, r.FormValue("databaseId"))
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
		if !deps.VerifyCSRF(w, r) {
			return
		}
		r.Body = http.MaxBytesReader(w, r.Body, 32<<10)
		if err := r.ParseForm(); err != nil {
			http.Error(w, "invalid form", http.StatusBadRequest)
			return
		}
		p, db, err := selectedDB(deps, r, r.FormValue("databaseId"))
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
		if !deps.Demo {
			client, err := client(deps, p)
			if err != nil {
				failed(http.StatusServiceUnavailable, "The live data plane is unavailable. Try again shortly.")
				return
			}
			result, err := client.ExecuteSQL(r.Context(), db.Name, statement, nil)
			if err != nil {
				failed(http.StatusBadGateway, "The query failed: "+err.Error())
				return
			}
			writeSQLResponse(w, r, deps, db.ID, statement, result, liveResult(result))
			return
		}
		matches := selectPattern.FindStringSubmatch(statement)
		if matches == nil {
			failed(http.StatusBadRequest, "The in-memory demo runs SELECT * FROM collection [LIMIT n] only. Check SQL validates any other read-only statement.")
			return
		}
		rows, matched, ok := demoSelect(db.Documents[matches[1]], matches[2])
		if !ok {
			failed(http.StatusBadRequest, "Demo queries return 1 to "+strconv.Itoa(maxDemoRows)+" rows; use a LIMIT in that range.")
			return
		}
		writeSQLResponse(w, r, deps, db.ID, statement, struct {
			Rows []documentView `json:"rows"`
		}{Rows: rows}, newSQLResult(rows, matched))
	})
	mux.HandleFunc("GET /api/v1/dashboard/data/saved-queries", func(w http.ResponseWriter, r *http.Request) {
		_, _, project, err := selectedProject(deps, r)
		if err != nil {
			writeError(w, err)
			return
		}
		queries := slices.AppendSeq(make([]core.SavedQuery, 0, len(project.SavedQueries)), maps.Values(project.SavedQueries))
		slices.SortFunc(queries, func(a, b core.SavedQuery) int { return strings.Compare(a.Name, b.Name) })
		core.WriteJSON(w, http.StatusOK, queries)
	})
	mux.HandleFunc("POST /api/v1/dashboard/data/saved-queries", func(w http.ResponseWriter, r *http.Request) {
		if !deps.VerifyCSRF(w, r) {
			return
		}
		if err := r.ParseForm(); err != nil {
			http.Error(w, "invalid form", http.StatusBadRequest)
			return
		}
		p, _, _, err := selectedProject(deps, r)
		if err != nil {
			writeError(w, err)
			return
		}
		if !deps.Store.Can(p, core.Organization.CanWrite) {
			writeError(w, core.ErrForbidden)
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
		query := core.SavedQuery{ID: core.NewID("qry"), Name: name, SQL: sql, CreatedAt: time.Now().UTC()}
		err = deps.Store.UpdateAs(p, func(a *core.Account) error {
			project, err := a.ProjectFor(p, p.OrganizationID, p.ProjectID)
			if err != nil {
				return err
			}
			if !a.Organizations[p.OrganizationID].CanWrite(p.UserID) {
				return core.ErrForbidden
			}
			if project.SavedQueries == nil {
				project.SavedQueries = make(map[string]core.SavedQuery)
			}
			project.SavedQueries[query.ID] = query
			org := a.Organizations[p.OrganizationID]
			org.Projects[p.ProjectID] = project
			a.Organizations[p.OrganizationID] = org
			return nil
		})
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
		if !deps.VerifyCSRF(w, r) {
			return
		}
		if err := r.ParseForm(); err != nil {
			http.Error(w, "invalid form", http.StatusBadRequest)
			return
		}
		p, _, _, err := selectedProject(deps, r)
		if err != nil {
			writeError(w, err)
			return
		}
		if !deps.Store.Can(p, core.Organization.CanWrite) {
			writeError(w, core.ErrForbidden)
			return
		}
		id := r.FormValue("id")
		err = deps.Store.UpdateAs(p, func(a *core.Account) error {
			project, err := a.ProjectFor(p, p.OrganizationID, p.ProjectID)
			if err != nil {
				return err
			}
			if !a.Organizations[p.OrganizationID].CanWrite(p.UserID) {
				return core.ErrForbidden
			}
			if _, ok := project.SavedQueries[id]; !ok {
				return core.ErrNotFound
			}
			delete(project.SavedQueries, id)
			org := a.Organizations[p.OrganizationID]
			org.Projects[p.ProjectID] = project
			a.Organizations[p.OrganizationID] = org
			return nil
		})
		if err != nil {
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

// writeSQLResponse answers a query run: API clients get the JSON result and
// the console shows it as a table below the statement.
func writeSQLResponse(w http.ResponseWriter, r *http.Request, deps core.Deps, databaseID, statement string, response any, result sqlResult) {
	if !core.WantsHTML(r) {
		core.WriteJSON(w, http.StatusOK, response)
		return
	}
	renderSection(w, r, deps, databaseID, "sql", http.StatusOK, func(d *pageData) {
		d.SQL, d.SQLResult = statement, &result
	})
}

// liveResult lays out a data plane result; an EXPLAIN answers with its plan.
func liveResult(result *sdk.SQLResult) sqlResult {
	rows := make([]documentView, 0, len(result.Rows))
	for _, row := range result.Rows {
		value, err := json.Marshal(row.Value)
		if err != nil {
			value = []byte("null")
		}
		rows = append(rows, documentView{ID: row.ID, Value: value})
	}
	table := newSQLResult(rows, int(min(result.Matched, math.MaxInt32)))
	table.Explain = result.Explain
	return table
}

// demoSelect answers SELECT * FROM collection [LIMIT n] over the demo
// documents in ID order, with the number of documents the query matched. An
// explicit LIMIT is part of the query, so it matches only the rows it keeps;
// without one the demo still returns at most maxDemoRows, and every document
// counts as matched so the result says it was cut. It reports false when the
// LIMIT is outside 1 to maxDemoRows.
func demoSelect(documents map[string][]byte, limit string) ([]documentView, int, bool) {
	rows := make([]documentView, 0, len(documents))
	for id, body := range documents {
		rows = append(rows, documentView{ID: id, Value: slices.Clone(body)})
	}
	slices.SortFunc(rows, func(a, b documentView) int { return strings.Compare(a.ID, b.ID) })
	if limit == "" {
		return rows[:min(len(rows), maxDemoRows)], len(rows), true
	}
	n, err := strconv.Atoi(limit)
	if err != nil || n < 1 || n > maxDemoRows {
		return nil, 0, false
	}
	rows = rows[:min(len(rows), n)]
	return rows, len(rows), true
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

func selectedProject(deps core.Deps, r *http.Request) (core.Principal, core.Account, core.Project, error) {
	p, ok := deps.Sessions.Principal(r)
	if !ok || p.ProjectID == "" {
		return core.Principal{}, core.Account{}, core.Project{}, core.ErrUnauthorized
	}
	a, err := deps.Store.SnapshotAs(p)
	if err != nil {
		return core.Principal{}, core.Account{}, core.Project{}, err
	}
	project, err := a.ProjectFor(p, p.OrganizationID, p.ProjectID)
	return p, a, project, err
}
