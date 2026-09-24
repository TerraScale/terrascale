package data

import (
	"embed"
	"encoding/json/jsontext"
	json "encoding/json/v2"
	"io/fs"
	"net/http"
	"slices"
	"strconv"
	"strings"
	"terrascale.tech/dashboard/internal/core"
	sdk "terrascale.tech/sdk"
)

//go:embed templates/*.html
var featureTemplates embed.FS

type databaseChoice struct {
	ID, Name string
}

type pageData struct {
	Mode       string
	ProjectID  string
	Databases  []databaseChoice
	DatabaseID string
	Collection string
	Documents  []documentView
	Schema     string
	Indexes    []string
	Queries    []core.SavedQuery
	DocumentID string
	History    []historyVersion
	Error      string
}

type historyVersion struct {
	Number  string
	Body    string
	Deleted bool
}

func mountPages(mux *http.ServeMux, deps core.Deps) {
	templates, err := fs.Sub(featureTemplates, "templates")
	if err != nil {
		panic(err)
	}
	if err := deps.Render.Register("data", templates, "data.html"); err != nil {
		panic(err)
	}
	for path, mode := range map[string]string{"/data": "documents", "/schema": "schema", "/indexes": "indexes", "/history": "history"} {
		mux.HandleFunc("GET "+path, func(w http.ResponseWriter, r *http.Request) { renderPage(w, r, deps, mode) })
	}
}

func renderPage(w http.ResponseWriter, r *http.Request, deps core.Deps, mode string) {
	p, project, err := selectedProject(deps, r)
	if err != nil {
		writeError(w, err)
		return
	}
	a, _ := deps.Store.SnapshotAs(p)
	data := pageData{Mode: mode, ProjectID: project.ID, DatabaseID: r.URL.Query().Get("databaseId"), Collection: r.URL.Query().Get("collection"), DocumentID: r.URL.Query().Get("id"), Error: r.URL.Query().Get("error")}
	for _, db := range project.Databases {
		data.Databases = append(data.Databases, databaseChoice{ID: db.ID, Name: db.Name})
	}
	slices.SortFunc(data.Databases, func(a, b databaseChoice) int { return strings.Compare(a.Name, b.Name) })
	if data.DatabaseID == "" && len(data.Databases) > 0 {
		data.DatabaseID = data.Databases[0].ID
	}
	if data.Collection == "" {
		data.Collection = "users"
	}
	if data.DatabaseID != "" {
		db, ok := project.Databases[data.DatabaseID]
		if !ok {
			writeError(w, core.ErrNotFound)
			return
		}
		if deps.Demo {
			for id, body := range db.Documents[data.Collection] {
				data.Documents = append(data.Documents, documentView{ID: id, Value: body})
			}
			slices.SortFunc(data.Documents, func(a, b documentView) int { return strings.Compare(a.ID, b.ID) })
			data.Schema = string(db.Schemas[data.Collection])
			data.Indexes = slices.Clone(db.Indexes[data.Collection])
			if mode == "history" && data.DocumentID != "" {
				for i, version := range db.DocumentHistory[data.Collection][data.DocumentID] {
					data.History = append(data.History, historyVersion{Number: strconv.Itoa(i + 1), Body: string(version), Deleted: jsontext.Value(version).Kind() == 'n'})
				}
			}
		} else if deps.SDK != nil {
			client, err := deps.SDK.ForScope(p.OrganizationID, p.ProjectID)
			if err == nil {
				switch mode {
				case "documents":
					rows, err := client.Documents.List(r.Context(), db.Name, data.Collection, 100, "")
					if err == nil {
						for _, row := range rows.Documents {
							data.Documents = append(data.Documents, documentView{ID: row.ID, Value: row.Value})
						}
					} else {
						data.Error = err.Error()
					}
				case "history":
					if data.DocumentID != "" {
						entries, err := client.Documents.History(r.Context(), db.Name, data.Collection, data.DocumentID)
						if err == nil {
							for _, entry := range entries {
								data.History = append(data.History, historyVersion{Number: entry.Revision, Deleted: entry.Deleted})
							}
						} else {
							data.Error = err.Error()
						}
					}
				case "indexes":
					page, err := client.Databases.ListIndexes(r.Context(), db.Name, data.Collection, sdk.PageRequest{})
					if err == nil {
						for _, index := range page.Indexes {
							data.Indexes = append(data.Indexes, index.Field)
						}
					} else {
						data.Error = err.Error()
					}
				case "schema":
					current, err := client.Databases.GetCollectionSchema(r.Context(), db.Name, data.Collection)
					if err == nil {
						encoded, encodeErr := json.Marshal(current.Schema)
						if encodeErr == nil {
							data.Schema = string(encoded)
						} else {
							data.Error = encodeErr.Error()
						}
					} else {
						data.Error = err.Error()
					}
				}
			} else {
				data.Error = "Live data plane unavailable."
			}
		} else {
			data.Error = "Live data plane unavailable."
		}
	}
	for _, query := range project.SavedQueries {
		data.Queries = append(data.Queries, query)
	}
	slices.SortFunc(data.Queries, func(a, b core.SavedQuery) int { return strings.Compare(a.Name, b.Name) })
	org := a.Organizations[p.OrganizationID]
	page := core.Page{Template: "data", Layout: "shell", Title: strings.ToUpper(mode[:1]) + mode[1:] + " · TerraScale", Principal: p, Data: data, CSRFToken: deps.CSRF.Token(w, r), Shell: core.Shell{UserName: a.User.Name, UserEmail: a.User.Email, OrganizationName: org.Name, ProjectName: project.Name}}
	if err := deps.Render.Page(w, r, page); err != nil {
		http.Error(w, "could not render data page", http.StatusInternalServerError)
	}
}
