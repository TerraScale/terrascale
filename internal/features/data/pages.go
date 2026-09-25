package data

import (
	"encoding/json/jsontext"
	json "encoding/json/v2"
	"maps"
	"net/http"
	"net/url"
	"slices"
	"strconv"
	"strings"

	"github.com/a-h/templ"
	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/features/databases"
	"terrascale.tech/dashboard/internal/features/notfound"
	sdk "terrascale.tech/sdk"
)

// Sections are the database detail tabs this slice renders. History is part
// of the documents tab.
var sections = map[string]struct{ Tab, Title string }{
	"documents": {"documents", "Documents"},
	"history":   {"documents", "Document history"},
	"sql":       {"sql", "SQL console"},
	"schema":    {"schema", "Schema"},
	"indexes":   {"indexes", "Indexes"},
}

// indexViews are the sub-views of the indexes tab.
var indexViews = []struct{ Key, Label string }{
	{"overview", "Overview"},
	{"templates", "Templates"},
	{"recommendations", "Recommendations"},
	{"migration", "Migration"},
}

type pageData struct {
	Section         string
	Database        core.Database
	CanWrite        bool
	CanManage       bool
	Demo            bool
	Search          string
	IndexView       string
	Collection      string
	Documents       []documentView
	Schema          string
	Indexes         []string
	IndexBuilds     []core.IndexBuild
	Recommendations []core.IndexRecommendation
	Migration       *demoIndexMigration
	Queries         []core.SavedQuery
	DocumentID      string
	EditJSON        string
	SQL             string
	SQLResult       string
	SQLAnalysis     sqlAnalysis
	SQLChecked      bool
	History         []historyVersion
	Error           string
}

type historyVersion struct {
	Number  string
	Body    string
	Deleted bool
}

func mountPages(mux *http.ServeMux, deps core.Deps) {
	for section := range sections {
		mux.HandleFunc("GET /databases/{databaseID}/"+section, func(w http.ResponseWriter, r *http.Request) {
			renderSection(w, r, deps, r.PathValue("databaseID"), section, nil)
		})
	}
}

// sectionPath is the URL of a data section of one database.
func sectionPath(databaseID, section string, query url.Values) string {
	return databases.TabPath(databaseID, section, query)
}

type sqlPageResult struct {
	Collection, Statement, JSON string
	CheckOnly                   bool
}

// renderSection renders one data section of a database. SQL form posts pass
// their result so the statement and its output never travel in the URL.
func renderSection(w http.ResponseWriter, r *http.Request, deps core.Deps, databaseID, section string, result *sqlPageResult) {
	p, a, project, err := selectedProject(deps, r)
	if err != nil {
		writeError(w, err)
		return
	}
	db, ok := project.Databases[databaseID]
	if !ok {
		notfound.Render(w, r)
		return
	}
	query := r.URL.Query()
	org := a.Organizations[p.OrganizationID]
	data := pageData{
		Section:    section,
		Database:   db,
		CanWrite:   org.CanWrite(p.UserID),
		CanManage:  org.CanManage(p.UserID),
		Demo:       deps.Demo,
		Search:     strings.TrimSpace(query.Get("q")),
		IndexView:  query.Get("view"),
		Collection: strings.TrimSpace(query.Get("collection")),
		DocumentID: query.Get("id"),
		Error:      query.Get("error"),
	}
	if result != nil && result.Collection != "" {
		data.Collection = result.Collection
	}
	if !validCollection(data.Collection) {
		data.Collection = "users"
	}
	if !slices.ContainsFunc(indexViews, func(v struct{ Key, Label string }) bool { return v.Key == data.IndexView }) {
		data.IndexView = "overview"
	}
	if len(data.Search) > 256 {
		data.Search = data.Search[:256]
	}
	if section == "documents" {
		data.DocumentID = query.Get("edit")
	}
	if deps.Demo {
		loadDemo(&data, a, p, db)
	} else {
		loadLive(r, deps, p, &data)
	}
	data.Queries = slices.SortedFunc(maps.Values(project.SavedQueries), func(a, b core.SavedQuery) int { return strings.Compare(a.Name, b.Name) })
	if section == "sql" {
		data.SQL = "SELECT * FROM " + data.Collection + " LIMIT 20"
		if saved, ok := project.SavedQueries[query.Get("savedQueryId")]; ok {
			data.SQL = saved.SQL
		}
		if result != nil {
			data.SQL = result.Statement
			data.SQLResult = result.JSON
		}
		data.SQLAnalysis = analyzeSQL(data.SQL)
		data.SQLChecked = result != nil && result.CheckOnly && data.SQLAnalysis.OK
	}
	if section == "documents" && data.DocumentID != "" {
		if i := slices.IndexFunc(data.Documents, func(d documentView) bool { return d.ID == data.DocumentID }); i >= 0 {
			data.EditJSON = data.Documents[i].Value.String()
		}
	}
	if search := strings.ToLower(data.Search); search != "" && section == "documents" {
		data.Documents = slices.DeleteFunc(data.Documents, func(document documentView) bool {
			return !strings.Contains(strings.ToLower(document.ID+" "+document.Value.String()), search)
		})
	}
	frame := databases.FrameProps{Database: db, Tab: sections[section].Tab, Collection: data.Collection}
	page := core.Page{Layout: "shell", Title: db.Name + " · " + sections[section].Title, Principal: p, View: func(page core.Page) templ.Component { return sectionPage(page, frame, data) }, CSRFToken: deps.CSRF.Token(w, r)}
	if err := deps.Render.Page(w, r, page); err != nil {
		http.Error(w, "could not render database data", http.StatusInternalServerError)
	}
}

// loadDemo reads the selected collection from the in-memory demo database.
func loadDemo(data *pageData, a core.Account, p core.Principal, db core.Database) {
	if data.Section == "indexes" && data.IndexView == "migration" {
		data.Migration = marioIndexMigration(a, p, db, data.Collection)
	}
	for id, body := range db.Documents[data.Collection] {
		data.Documents = append(data.Documents, documentView{ID: id, Value: body})
	}
	slices.SortFunc(data.Documents, func(a, b documentView) int { return strings.Compare(a.ID, b.ID) })
	data.Schema = string(db.Schemas[data.Collection])
	data.Indexes = slices.Clone(db.Indexes[data.Collection])
	for _, build := range db.IndexBuilds {
		if build.Collection == data.Collection {
			data.IndexBuilds = append(data.IndexBuilds, build)
		}
	}
	for _, recommendation := range db.IndexRecommendations {
		if recommendation.DatabaseID == db.ID && recommendation.Collection == data.Collection {
			data.Recommendations = append(data.Recommendations, recommendation)
		}
	}
	if data.Section == "history" && data.DocumentID != "" {
		for i, version := range db.DocumentHistory[data.Collection][data.DocumentID] {
			data.History = append(data.History, historyVersion{Number: strconv.Itoa(i + 1), Body: string(version), Deleted: jsontext.Value(version).Kind() == 'n'})
		}
	}
}

// loadLive reads what the open section needs through the data plane SDK.
func loadLive(r *http.Request, deps core.Deps, p core.Principal, data *pageData) {
	client, err := client(deps, p)
	if err != nil {
		data.Error = "Live data plane unavailable."
		return
	}
	name := data.Database.Name
	switch data.Section {
	case "documents":
		rows, err := client.Documents.List(r.Context(), name, data.Collection, 100, "")
		if err != nil {
			data.Error = err.Error()
			return
		}
		for _, row := range rows.Documents {
			data.Documents = append(data.Documents, documentView{ID: row.ID, Value: row.Value})
		}
	case "history":
		if data.DocumentID == "" {
			return
		}
		entries, err := client.Documents.History(r.Context(), name, data.Collection, data.DocumentID)
		if err != nil {
			data.Error = err.Error()
			return
		}
		for _, entry := range entries {
			data.History = append(data.History, historyVersion{Number: entry.Revision, Deleted: entry.Deleted})
		}
	case "indexes":
		page, err := client.Databases.ListIndexes(r.Context(), name, data.Collection, sdk.PageRequest{})
		if err != nil {
			data.Error = err.Error()
			return
		}
		for _, index := range page.Indexes {
			data.Indexes = append(data.Indexes, index.Field)
		}
	case "schema":
		current, err := client.Databases.GetCollectionSchema(r.Context(), name, data.Collection)
		if err != nil {
			data.Error = err.Error()
			return
		}
		encoded, err := json.Marshal(current.Schema)
		if err != nil {
			data.Error = err.Error()
			return
		}
		data.Schema = string(encoded)
	}
}
