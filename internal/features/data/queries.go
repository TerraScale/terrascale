package data

import (
	json "encoding/json/v2"
	"maps"
	"net/http"
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
		_, _, _, err := selectedProject(deps, r)
		if err != nil {
			writeError(w, err)
			return
		}
		databaseID := r.FormValue("databaseId")
		if databaseID != "" {
			_, db, err := selectedDB(deps, r, databaseID)
			if err != nil {
				writeError(w, err)
				return
			}
			databaseID = db.ID
		}
		statement := r.FormValue("sql")
		if len(statement) > maxSQLCheckBytes {
			http.Error(w, "SQL must be 16 KiB or less", http.StatusBadRequest)
			return
		}
		collection := strings.TrimSpace(r.FormValue("collection"))
		if !validCollection(collection) {
			http.Error(w, "invalid collection", http.StatusBadRequest)
			return
		}
		renderPageWithSQLResult(w, r, deps, "sql", &sqlPageResult{
			DatabaseID: databaseID,
			Collection: collection,
			Statement:  statement,
			CheckOnly:  true,
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
			writeSQLResponse(w, r, deps, db.ID, statement, result)
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
		writeSQLResponse(w, r, deps, db.ID, statement, struct {
			Rows []documentView `json:"rows"`
		}{Rows: rows})
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
		if core.WantsHTML(r) {
			http.Redirect(w, r, "/data?view=queries", http.StatusSeeOther)
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
			writeError(w, err)
			return
		}
		if core.WantsHTML(r) {
			http.Redirect(w, r, "/data?view=queries", http.StatusSeeOther)
			return
		}
		w.WriteHeader(http.StatusNoContent)
	})
}

func writeSQLResponse(w http.ResponseWriter, r *http.Request, deps core.Deps, databaseID, statement string, result any) {
	if !core.WantsHTML(r) {
		core.WriteJSON(w, http.StatusOK, result)
		return
	}
	encoded, err := json.Marshal(result)
	if err != nil {
		http.Error(w, "encode response failed", http.StatusInternalServerError)
		return
	}
	renderPageWithSQLResult(w, r, deps, "sql", &sqlPageResult{DatabaseID: databaseID, Statement: statement, JSON: string(encoded)})
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
