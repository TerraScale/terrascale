package data

import (
	"net/http"
	"slices"
	"strconv"
	"strings"
	"time"

	"terrascale.tech/dashboard/internal/core"
	sdk "terrascale.tech/sdk"
)

func mountQueryRoutes(mux *http.ServeMux, deps core.Deps) {
	mux.HandleFunc("POST /api/v1/dashboard/data/sql", func(w http.ResponseWriter, r *http.Request) {
		if err := deps.CSRF.Verify(r); err != nil {
			http.Error(w, "invalid CSRF token", http.StatusForbidden)
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
		if len(statement) == 0 || len(statement) > 16<<10 || sdk.ValidateSQL(statement, nil) != nil {
			http.Error(w, "enter one read-only SQL query", http.StatusBadRequest)
			return
		}
		if !deps.Demo {
			client, err := client(deps, p)
			if err != nil {
				writeError(w, err)
				return
			}
			result, err := client.ExecuteSQL(r.Context(), db.Name, statement, nil)
			if err != nil {
				http.Error(w, "SQL query failed: "+err.Error(), http.StatusBadGateway)
				return
			}
			writeJSON(w, http.StatusOK, result)
			return
		}
		matches := selectPattern.FindStringSubmatch(statement)
		if matches == nil {
			http.Error(w, "demo SQL supports SELECT * FROM collection [LIMIT n]", http.StatusBadRequest)
			return
		}
		limit := 100
		if matches[2] != "" {
			limit, _ = strconv.Atoi(matches[2])
		}
		if limit < 1 || limit > 100 {
			http.Error(w, "query limit must be 1 to 100", http.StatusBadRequest)
			return
		}
		rows := make([]documentView, 0, len(db.Documents[matches[1]]))
		for id, body := range db.Documents[matches[1]] {
			rows = append(rows, documentView{ID: id, Value: slices.Clone(body)})
		}
		slices.SortFunc(rows, func(a, b documentView) int { return strings.Compare(a.ID, b.ID) })
		if len(rows) > limit {
			rows = rows[:limit]
		}
		writeJSON(w, http.StatusOK, struct {
			Rows []documentView `json:"rows"`
		}{Rows: rows})
	})
	mux.HandleFunc("GET /api/v1/dashboard/data/saved-queries", func(w http.ResponseWriter, r *http.Request) {
		_, project, err := selectedProject(deps, r)
		if err != nil {
			writeError(w, err)
			return
		}
		queries := make([]core.SavedQuery, 0, len(project.SavedQueries))
		for _, query := range project.SavedQueries {
			queries = append(queries, query)
		}
		slices.SortFunc(queries, func(a, b core.SavedQuery) int { return strings.Compare(a.Name, b.Name) })
		writeJSON(w, http.StatusOK, queries)
	})
	mux.HandleFunc("POST /api/v1/dashboard/data/saved-queries", func(w http.ResponseWriter, r *http.Request) {
		if err := deps.CSRF.Verify(r); err != nil {
			http.Error(w, "invalid CSRF token", http.StatusForbidden)
			return
		}
		if err := r.ParseForm(); err != nil {
			http.Error(w, "invalid form", http.StatusBadRequest)
			return
		}
		p, _, err := selectedProject(deps, r)
		if err != nil {
			writeError(w, err)
			return
		}
		if !canWrite(deps, p) {
			writeError(w, core.ErrForbidden)
			return
		}
		name, sql := strings.TrimSpace(r.FormValue("name")), strings.TrimSpace(r.FormValue("sql"))
		if name == "" || len(name) > 128 || sql == "" || len(sql) > 16<<10 || sdk.ValidateSQL(sql, nil) != nil {
			http.Error(w, "invalid saved query", http.StatusBadRequest)
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
			writeError(w, err)
			return
		}
		writeJSON(w, http.StatusCreated, query)
	})
	mux.HandleFunc("POST /api/v1/dashboard/data/saved-queries/delete", func(w http.ResponseWriter, r *http.Request) {
		if err := deps.CSRF.Verify(r); err != nil {
			http.Error(w, "invalid CSRF token", http.StatusForbidden)
			return
		}
		if err := r.ParseForm(); err != nil {
			http.Error(w, "invalid form", http.StatusBadRequest)
			return
		}
		p, _, err := selectedProject(deps, r)
		if err != nil {
			writeError(w, err)
			return
		}
		if !canWrite(deps, p) {
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
			writeError(w, err)
			return
		}
		w.WriteHeader(http.StatusNoContent)
	})
}

func selectedProject(deps core.Deps, r *http.Request) (core.Principal, core.Project, error) {
	p, ok := deps.Sessions.Principal(r)
	if !ok || p.ProjectID == "" {
		return core.Principal{}, core.Project{}, core.ErrUnauthorized
	}
	a, err := deps.Store.SnapshotAs(p)
	if err != nil {
		return core.Principal{}, core.Project{}, err
	}
	project, err := a.ProjectFor(p, p.OrganizationID, p.ProjectID)
	return p, project, err
}
